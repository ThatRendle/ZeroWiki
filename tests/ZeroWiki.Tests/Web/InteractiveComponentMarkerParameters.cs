using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Reflective access to the framework's <c>internal</c> component-parameter deserializer, isolated
/// here so any future block that needs to inspect another interactive component's parameters does not
/// have to re-derive the call shape documented on
/// <see cref="ChangedOnDiskIndicatorRouteRoundTripTests"/>.
/// </summary>
internal static class InteractiveComponentMarkerParameters
{
    /// <summary>
    /// Locates the single <c>&lt;!--Blazor:{...}--&gt;</c> open marker for <paramref name="componentType"/>
    /// in <paramref name="html"/>, replays it through the host's own component deserializer exactly as
    /// a real circuit start would, and returns the reconstructed value of the named parameter.
    /// </summary>
    public static T GetParameterValue<T>(
        WebApplicationFactory<Program> app, string html, Type componentType, string parameterName)
    {
        var markerJson = ExtractSingleMarkerJson(html);

        var deserializerAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "Microsoft.AspNetCore.Components.Server");
        var deserializerInterface = deserializerAssembly.GetType(
            "Microsoft.AspNetCore.Components.Server.IServerComponentDeserializer")
            ?? throw new InvalidOperationException(
                "IServerComponentDeserializer not found -- the framework's internal shape moved.");
        var method = deserializerInterface.GetMethod("TryDeserializeComponentDescriptorCollection")
            ?? throw new InvalidOperationException(
                "TryDeserializeComponentDescriptorCollection not found -- the framework's internal shape moved.");

        using var scope = app.Services.CreateScope();
        var deserializer = scope.ServiceProvider.GetService(deserializerInterface)
            ?? throw new InvalidOperationException("IServerComponentDeserializer is not registered in this host.");

        var args = new object?[] { "[" + markerJson + "]", null };
        var ok = (bool)method.Invoke(deserializer, args)!;
        Assert.True(ok, "the framework's own deserializer rejected the marker it just emitted");

        var descriptors = (System.Collections.IEnumerable)args[1]!;
        var descriptorType = deserializerAssembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentDescriptor")
            ?? throw new InvalidOperationException("ComponentDescriptor not found -- the framework's internal shape moved.");
        var componentTypeProperty = descriptorType.GetProperty("ComponentType")
            ?? throw new InvalidOperationException("ComponentType not found -- the framework's internal shape moved.");
        var parametersProperty = descriptorType.GetProperty("Parameters")
            ?? throw new InvalidOperationException("Parameters not found -- the framework's internal shape moved.");

        var matches = descriptors.Cast<object>()
            .Where(d => (Type)componentTypeProperty.GetValue(d)! == componentType)
            .ToList();
        Assert.Single(matches);

        var parameters = parametersProperty.GetValue(matches[0])!;
        var parameterViewType = parameters.GetType();
        var enumerator = parameterViewType.GetMethod("GetEnumerator")!.Invoke(parameters, null)!;
        var enumeratorType = enumerator.GetType();
        var moveNext = enumeratorType.GetMethod("MoveNext")!;
        var current = enumeratorType.GetProperty("Current")!;

        while ((bool)moveNext.Invoke(enumerator, null)!)
        {
            var entry = current.GetValue(enumerator)!;
            var entryType = entry.GetType();
            var name = (string)entryType.GetProperty("Name")!.GetValue(entry)!;
            if (name == parameterName)
            {
                var valueProperty = entryType.GetProperty("Value")
                    ?? throw new InvalidOperationException("Value not found -- the framework's internal shape moved.");
                return (T)valueProperty.GetValue(entry)!;
            }
        }

        throw new InvalidOperationException($"No parameter named '{parameterName}' on {componentType}.");
    }

    private static string ExtractSingleMarkerJson(string html)
    {
        const string openPrefix = "<!--Blazor:";
        var start = html.IndexOf(openPrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "no InteractiveServer component marker found in the response");

        var jsonStart = start + openPrefix.Length;
        var jsonEnd = html.IndexOf("-->", jsonStart, StringComparison.Ordinal);
        Assert.True(jsonEnd >= 0, "unterminated InteractiveServer component marker");

        return html[jsonStart..jsonEnd];
    }
}
