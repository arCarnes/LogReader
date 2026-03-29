# Scope-Owned Search/Filter Scratchpad

Date: 2026-03-27
Status: Deferred

## Summary

We explored changing Search and Filter so the right-hand pane behaves like a transient scratchpad owned by the active scope (`Dashboard` or `Ad Hoc`) instead of the selected tab.

The original motivation was a UI mismatch:

- Filtering currently applies only to the selected tab.
- The filter form is shared across tabs.
- Switching to a different tab can leave the filter pane looking "active" even when the new tab is not filtered.

## Proposed Behavior

The proposal we worked through was:

- Filter belongs to the active scope, not the selected tab.
- Switching tabs inside the same scope keeps the filter/search scratchpad active.
- Switching to a different dashboard or to Ad Hoc clears the old scope scratchpad.
- Search follows the same scratchpad lifetime rules.
- Search keeps both `Current file` and `Current scope`.
- Filter becomes scope-wide across the visible tabs in the active scope.
- None of this state is persisted to dashboard JSON, app settings, import/export data, or recovery state.

We also decided that if a scope-wide filter hits mixed results, it should partially apply:

- Successful tabs become filtered.
- Failed tabs remain unfiltered.
- The filter pane would report a scope-level warning instead of failing the whole operation.

## Why We Paused It

After planning it out, the original premise started to feel less certain.

The current behavior may already be intuitive enough if users treat Filter as a per-tab action and simply press `Apply Filter` again after switching tabs. That is a much smaller mental model change than turning Search/Filter into scope-owned workspace tools.

The scope-scratchpad design is coherent, but it is also a real product shift:

- It changes Filter from a file-level tool into a workspace-level tool.
- It introduces live scope membership rules for tabs entering or leaving the current scope.
- It requires stronger coordination between scope changes, search tail tracking, and per-tab filter state.

Because of that, this felt worth saving as a future design note rather than implementing immediately.

## If Revisited Later

If we return to this idea, the latest agreed direction was:

1. Treat Search/Filter as transient scratchpad state owned by the active scope.
2. Clear that scratchpad on any real scope switch or when the current scope becomes empty.
3. Keep Go To separate from scratchpad resets.
4. Keep Search modes as `Current file` and `Current scope`.
5. Make Filter scope-wide and use partial-apply behavior for per-file failures.

At the time this note was written, no implementation work had been started for the feature branch that explored this idea.
