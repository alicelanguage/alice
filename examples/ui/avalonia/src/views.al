# Code-behind: each view only loads its XAML. Behaviour lives in the view models (viewmodels.al).
using Avalonia
using Avalonia.Controls
using Avalonia.Controls.ApplicationLifetimes
using Avalonia.Markup.Xaml
using Avalonia.Themes.Simple

namespace Notes
  class MainWindow < Window
    def initialize
      AvaloniaXamlLoader.Load(self)
    end
  end

  class NotesView < UserControl
    def initialize
      AvaloniaXamlLoader.Load(self)
    end
  end

  class SettingsView < UserControl
    def initialize
      AvaloniaXamlLoader.Load(self)
    end
  end

  class App < Application
    def Initialize
      Styles.Add(SimpleTheme.new)
    end

    # Desktop start: one main window bound to a MainViewModel over the user's settings file.
    def OnFrameworkInitializationCompleted
      lifetime = ApplicationLifetime
      if lifetime.is_a?(IClassicDesktopStyleApplicationLifetime)
        window = MainWindow.new
        window.DataContext = MainViewModel.new(SettingsStore.new(SettingsStore.default_path))
        lifetime.as(IClassicDesktopStyleApplicationLifetime).MainWindow = window
      end
      super
    end
  end
end
