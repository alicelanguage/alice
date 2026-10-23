# A XAML window with its code-behind in Alice (Avalonia): MainWindow.axaml declares the controls, this class
# drives them. Runs headless here (Avalonia.Headless); for a desktop window use UsePlatformDetect and
# StartWithClassicDesktopLifetime instead.
using Avalonia
using Avalonia.Controls
using Avalonia.Headless
using Avalonia.Markup.Xaml

namespace Ava
  class MainWindow < Window
    @clicks : Int32 = 0
    def initialize
      AvaloniaXamlLoader.Load(self)
    end
    def bump
      @clicks += 1
      greeting.Text = "clicked #{@clicks}"
    end
    def greeting : TextBlock
      FindControl<TextBlock>("Greeting").not_nil!
    end
  end

  class App < Application
  end
end

AppBuilder.Configure<Ava::App>().UseHeadless(AvaloniaHeadlessPlatformOptions.new).SetupWithoutStarting
w = Ava::MainWindow.new
w.Show
w.bump
w.bump
puts w.greeting.Text
