# Easy Reading — Color Target

Status: visual direction and colors selected on 2026-09-24; implemented as the light palette on 2026-09-25 and retained as the Easy Reading option on 2026-09-26. The original light palette is the Default option.

Reference: [darker light-mode mockup](softer-light-mode-mockup.png), based on the user's current-light-mode screenshot. The generated image has minor gradients and text artifacts; the colors below are the intended flat UI values. Preserve the application's actual layout and text.

| UI role | Target |
| --- | --- |
| Main log viewport content and empty viewport | `#E3E8EC` |
| Search results content, including empty space | `#E0E5EA` |
| Dashboard list content, including empty space | `#D9E0E6` |
| Window canvas around panes | `#E0E5EA` |
| Title bar, top command bar, viewport chrome, dashboard header, search chrome, status bar | `#D8DFE5` |
| Secondary surfaces such as file headers and branch rows | `#E8EDF1` |
| Search field, buttons, and other raised input surfaces | `#F1F3F6` |
| Main pane borders and splitters | `#BCC8D2` |
| Subtle row dividers | `#CBD4DD` |

Keep existing light-mode text colors (`#1F2937` primary, `#5B6470` muted) and existing blue selection, focus, hover, and pressed colors. In particular, retain the current selected-row `#EAF4FE`, selected-member `#CFE1F4`, and viewport-selection `#B0D4FF` values. Preserve user-selected search highlight colors and the dark palette.

The dashboard pane is deliberately a little darker than the viewport and results, matching the approved mockup and making the left navigation distinct. The implementation and validation record are in the existing appearance execution plan.

The viewport was lightened on 2026-09-27 by blending its former `#E0E5EA` background 10% toward white. Other Easy Reading surfaces retain the original approved values.
