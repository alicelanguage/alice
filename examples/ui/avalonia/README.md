# Alice Notes: an Avalonia desktop app

A small cross-platform desktop app in Alice on [Avalonia](https://avaloniaui.net): two views (notes and
settings), MVVM with data binding and commands, and settings saved to a JSON file. The windows can be driven
headless, so the app is checked on a machine without a display.

| File | Role |
|---|---|
| [src/viewmodels.al](src/viewmodels.al) | `ViewModelBase` (`INotifyPropertyChanged`), `RelayCommand` (`ICommand`), `NotesViewModel`, `SettingsViewModel`, `MainViewModel` |
| [src/settings.al](src/settings.al) | `Settings` record and `SettingsStore`: load (defaults when missing or broken) and save, `System.Text.Json` |
| [src/views.al](src/views.al) | Code-behind: `MainWindow`, `NotesView`, `SettingsView` load their XAML; `App` sets the theme and the main window |
| [src/MainWindow.axaml](src/MainWindow.axaml) | Tab buttons, a title, and a `ContentControl` that shows the current page through `DataTemplates` |
| [src/NotesView.axaml](src/NotesView.axaml), [src/SettingsView.axaml](src/SettingsView.axaml) | The two views, bound to their view models |
| [src/main.al](src/main.al) | Desktop start, and the `--headless` run |
| [test/viewmodels_test.al](test/viewmodels_test.al) | View models and settings, without any window |

## MVVM in Alice

- A bound member is a `getter` (one-way) or `property` (two-way: the text box writes it). Bindings read CLR
  properties; a `def name=` writer compiles to a method, which bindings do not see.
- When a view model changes one of its own properties it calls `changed("name")`, which raises
  `PropertyChanged`; the bound controls read the property again.
- Navigation is data: `MainViewModel.current` is a view model, and `MainWindow.axaml` maps each view model type to
  its view with a `DataTemplate`.
- Buttons bind to `RelayCommand`s that run Alice blocks, so tests call `save_command.Execute(nil)` exactly as a
  click does.

## Run

From the repository root, with the .NET 10 SDK:

```bash
dotnet run --project src/Alice.Cli -- run examples/ui/avalonia
```

opens the window (Windows, macOS, or Linux with a display). Settings are saved to
`~/.config/alice-notes/settings.json` (`%APPDATA%\alice-notes\settings.json` on Windows).

Headless, with the settings in a directory of your choice:

```bash
dotnet run --project src/Alice.Cli -- run examples/ui/avalonia -- --headless /tmp/alice-notes
```

```text
title: Notes for friend
notes view: 2 items, "2 notes"
settings view: saved=true, title: Notes for Alice
count visible: false
after restart: title: Notes for Alice, name box: Alice
```

The headless run uses the real windows and XAML: it types into the text boxes, presses buttons (their commands),
switches views, saves the settings, then opens a second window that reads them back.

## Test

```bash
cd examples/ui/avalonia && alice test
```

Five specs on the view models and the settings file. `alice test` compiles the sources again without the XAML
compiler, so specs that need the XAML views run as the `--headless` mode above;
`SampleTests.Avalonia_notes_app_mvvm_two_views_and_saved_settings_headless` checks both.

The `Avalonia.Desktop` package provides the desktop window (`UsePlatformDetect`); `Avalonia.Headless` the
headless platform.
