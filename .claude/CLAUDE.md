## C# navigation — use `roslyn-codelens`, not grep

The `roslyn-codelens` MCP server replaced the codegraph server on 2026-08-16 (`4594392`).
For any question about C# in this repo, **reach for it before `grep`/`find`/`Read`** — it answers from
the compiler's own model, so it follows interface dispatch, generics, and partial classes that text
search cannot:

| Question | Tool |
|---|---|
| Does anything test this? | `find_tests_for_symbol`, `find_uncovered_symbols` |
| Who calls / references this? | `find_callers`, `find_references` |
| What is this, and where is it defined? | `get_symbol_context`, `go_to_definition`, `get_type_overview` |
| What's in this file? | `get_file_overview` |
| Is anything unused or dead? | `find_unused_symbols` |
| Does it compile clean? | `get_diagnostics` |
| What do the tests say? | `get_test_summary`, `find_tests_for_symbol` |

**Grep is still correct for what is genuinely text**, and saying so is the point — a rule that pretends
otherwise just produces a different blind instrument. Use it for: string and message literals, comment
and doc content, `.csproj`/JSON/YAML/Markdown, and *process-spawn arguments* (`new ProcessStartInfo("git"
…)` — the compiler sees a `string`, not a program being launched). When you use it for one of these, say
so, rather than letting it look like a symbol search done the lazy way.

**And know what `roslyn-codelens` cannot see.** It answers about *this solution's* source. It cannot tell
you what a third-party library does at runtime, whether a test's assertion can actually fail, or whether
any code path reaches a symbol at runtime as opposed to on paper. `find_tests_for_symbol` returning a
test proves a test *exists* — never that it would *die* if the behaviour were broken. That question is
still answered by breaking the property and watching a test fail; see the `mutation-testing` skill.
