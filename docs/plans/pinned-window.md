# Pinned window (keep on top)

Status: proposed, not started.

## Goal

Let users keep Clay above other windows without PowerToys' Always On Top, using a pin icon in the title bar next to the minimize button.

## Design

- **Pinning:** set `((OverlappedPresenter)AppWindow.Presenter).IsAlwaysOnTop`.
- **Setting:** add `bool AlwaysOnTop = false` to the `Settings` record in `AppData.cs`. The default keeps older settings files loading, as `PhysicsUnits` does.
- **Control:** a borderless, transparent `ToggleButton` with a `FontIcon` in the `AppTitleBar` grid in `MainWindow.xaml`, right-aligned.
  - Glyph: Segoe Fluent Icons pin `&#xE718;`, filled `&#xE840;` when pinned.
  - Tooltip: "Keep on top".
- **Placement:** the caption buttons are drawn by Windows, so use `AppWindow.TitleBar.RightInset` as the button's right margin. Update it on `SizeChanged` so DPI changes are followed.
- **Theming:** in `ApplyTitleBarTheme`, use cream glyphs and the brown hover color for the Sticky theme. Other themes use the default theme resources.
- **Persistence:** load the setting in `LoadSettings` (set the presenter and the toggle state) and write it in `SaveSettings`.

## Notes

- PowerToys Always On Top and the built-in pin both set the topmost flag, so they don't conflict. Turning one off while the other is on may unpin the window.
- The title bar is the drag region. Confirm in the running app that the button is clickable and that dragging still works around it.

## Tasks

- [ ] `AlwaysOnTop` setting with a default, plus round-trip and older-file tests in `AppDataTests.cs`
- [ ] Pin button in the title bar, placed with `RightInset`
- [ ] Presenter wiring in `LoadSettings`, `SaveSettings` and the click handler
- [ ] Sticky theme colors for the button
- [ ] Manual check: placement, hover, click, drag, DPI change, restart persistence
- [ ] README and CHANGELOG entries
