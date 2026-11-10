---
description: "Use when creating, modifying, reviewing, or testing Angular code in Examinator. Require modern Angular v22-compatible APIs, signals, standalone components, and built-in template control flow."
applyTo: "src/**/*.ts,src/**/*.html"
---
# Angular v22: modern, signal-first code

## Compatibility and scope
- All new or modified Angular code must use modern patterns compatible with the repository's installed Angular v22 packages. Check `package.json` and the installed API types before using an unfamiliar API; do not assume the latest online documentation targets v22.
- Use stable, public APIs. Do not introduce deprecated, preview, or experimental APIs without explicit approval.
- Do not copy legacy patterns merely because they exist nearby. Modernize the code needed for the requested change without unrelated migrations, dependency upgrades, or behavioral changes.

## Reactive state
- Use `signal()` for mutable UI/application state and `computed()` for derived values. Keep immutable constants and non-reactive implementation details as ordinary fields.
- Update signals with `set()` or `update()` and immutable object/array replacements; do not mutate a signal's value in place.
- Expose service-owned state through readonly signals and explicit update methods rather than publicly writable signals.
- Use `linkedSignal()` when writable state must reset or reconcile with reactive dependencies, rather than copying state in an effect.
- Reserve `effect()` for genuine imperative side effects, such as integrating PDF.js or browser APIs. Do not use effects to propagate or derive state. Register cleanup for cancellable work, listeners, and external resources.
- Keep RxJS for asynchronous streams where its operators are appropriate, such as cancellation and composition. Do not introduce `Subject` or `BehaviorSubject` for ordinary UI state.
- Bridge observables and signals with `toSignal()` or `toObservable()` at clear boundaries; avoid repeatedly creating bridges in getters or templates. Use `takeUntilDestroyed()` for imperative subscriptions and provide the correct injection context or `DestroyRef`.
- Preserve loading, empty, and error states explicitly. Cancel obsolete asynchronous work and prevent stale results from replacing current state.

## Components and dependency injection
- Use standalone components, directives, and pipes with explicit template imports; do not introduce NgModules. Omit redundant `standalone: true` where v22 already defaults to standalone.
- Use `ChangeDetectionStrategy.OnPush` for new components and ensure updates work with zoneless change detection. Do not introduce Zone.js dependencies, timer-based refresh hacks, or routine manual change detection.
- Use `inject()` instead of constructor parameter injection in new code.
- Use `input()` / `input.required()` for inputs, `output()` for outputs, and `model()` only for intentional two-way component contracts. Do not introduce decorator-based `@Input()` / `@Output()` or `EventEmitter`.
- Use signal queries (`viewChild()`, `viewChildren()`, `contentChild()`, `contentChildren()`, including required variants when appropriate) instead of decorator-based queries.
- Keep template-facing members `protected` and implementation details `private` where appropriate. Mark signal references, injected dependencies, inputs, outputs, and queries `readonly` when not reassigned.
- Prefer component/directive `host` metadata over `@HostBinding()` and `@HostListener()`.

## Templates and styling
- Use built-in `@if`, `@for`, and `@switch` control flow, not `*ngIf`, `*ngFor`, or `*ngSwitch`. Give every `@for` a stable, meaningful `track` expression; use an index only for truly static collections.
- Read signals by calling them in templates. Keep bindings simple and put non-trivial derived logic in `computed()` rather than expensive template methods.
- Prefer direct class and style bindings over `NgClass` and `NgStyle`.
- Preserve accessible semantics, keyboard interaction, focus handling, and responsive layouts. Use SCSS, not new plain CSS stylesheets.

## Verification
- Add or update focused tests for changed behavior, including signal-derived state, component input/output contracts, asynchronous cancellation, and cleanup where applicable.
- Tests must verify rendered updates through supported Angular scheduling and testing APIs rather than relying on Zone.js or arbitrary delays.
- Run `npm run build` after Angular code changes and relevant tests with `npm test`. Report failures or checks that could not be run; instruction-only edits do not require application builds.

## References
- [Angular style guide](https://angular.dev/style-guide)
- [Signals](https://angular.dev/guide/signals)
- [RxJS interop](https://angular.dev/ecosystem/rxjs-interop)
- [Zoneless Angular](https://angular.dev/guide/zoneless)
