# ADR 0005: Code style and English UI

## Status

Accepted

## Context

A codebase is easier to read and review when every file follows the same style and a field can never be mistaken for a local variable or a parameter. The product and its code must also be usable by any team and any customer.

## Decision

- C# injects dependencies through an ordinary constructor into `private readonly` fields named in camelCase without an underscore.
- Every access to a field is qualified with `this.` (for example `this.userService`).
- Primary constructors are not used.
- UI text, code and documentation are written in English.

## Rationale

One style throughout means every file reads the same, a field never looks like a local or a parameter, and the IDE can enforce the style through `.editorconfig`. A primary constructor's parameters cannot be private camelCase fields reached only through `this.`, which is why primary constructors are excluded. English makes the product and its code usable for any team and customer.

## Consequences

- The build enforces the field rules in every project: `.editorconfig` sets `dotnet_style_qualification_for_field` with IDE0009 and the camelCase naming rule for private instance fields with IDE1006 at warning, and `Directory.Build.props` sets `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`, so a field without `this.` or with a leading underscore fails the build.
- No analyzer forbids a primary constructor, and the analyzers skip the code Razor generates, so `CodeStyleScanTests` holds the rest: no class or struct has a primary constructor, no static field and no field of a component starts with an underscore, and every field of a component is reached through `this.`.
- Razor components keep their `[Inject]` properties (PascalCase); their private fields follow the field rule.
- No label, state name or dialog title is written in another language.

## Related Decisions

- [ADR 0027](0027-generic-resource-staffing.md): the neutral domain vocabulary used in code and UI.
