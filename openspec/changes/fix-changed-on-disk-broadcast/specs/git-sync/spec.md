## ADDED Requirements

### Requirement: The broadcast is established through the push endpoint, not around it

The system SHALL notify a connected viewer of a page when a push that touches that page is received
**through the Smart HTTP receive-pack endpoint** — the path a real git client takes. The guarantee is
end-to-end: it SHALL NOT be established by exercising the reaction service or the notifier in
isolation, because a push that updates the working tree while notifying nobody satisfies every such
part-wise check and still fails the requirement it is meant to prove.

Where the endpoint receives a push that advances `HEAD` and changes one or more page files, the system
SHALL invoke every subscriber registered under each changed page's route. A push that changes no page
file, or that leaves `HEAD` where it was, SHALL notify nobody.

#### Scenario: A push through the endpoint reaches a subscriber

- **WHEN** a subscriber is registered under a page's route, and a git client pushes a commit changing
  that page through the Smart HTTP receive-pack endpoint
- **THEN** that subscriber is invoked — established by driving the endpoint, not by calling the
  reaction service or the notifier directly

#### Scenario: A push that touches no page notifies nobody

- **WHEN** a push through the same endpoint advances `HEAD` but changes only files that are not pages
- **THEN** no subscriber is invoked, and the reaction reports that it matched nothing rather than
  failing

### Requirement: The push reaction's outcome is observable

The system SHALL record, for every push reaction it runs, what the reaction did — the number of routes
the diff named and the number of subscribers invoked — at a level visible in the application's ordinary
output.

A reaction that names no routes, and a reaction that names routes but matches no subscriber, SHALL be
distinguishable from one that delivered, and SHALL be distinguishable from each other. Logging only on
the failure path is insufficient: it makes a broadcast that reached nobody indistinguishable from a
correct delivery, and an operator cannot tell whether the reaction ran at all.

#### Scenario: A delivery that reaches nobody is visible

- **WHEN** a push reaction completes having invoked no subscriber, whether because the diff named no
  page routes or because no viewer was subscribed to the routes it named
- **THEN** the application's output says so, naming which of the two occurred — rather than being
  silent and therefore identical to a successful delivery
