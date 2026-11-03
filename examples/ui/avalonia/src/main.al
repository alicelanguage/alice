# Alice Notes: a small Avalonia app. Two views (notes, settings) chosen by MainViewModel.current, MVVM bindings,
# settings saved as JSON.
#
#   alice run examples/ui/avalonia                          open the window (needs a display)
#   alice run examples/ui/avalonia -- --headless DIR        drive the real windows headless, print what they show
using System.IO
using System.Linq
using Avalonia
using Avalonia.Controls
using Avalonia.Headless
using Avalonia.Threading
using Avalonia.VisualTree
using Notes

# Finds a named control inside the page currently shown by the window.
def control(window : MainWindow, name : String) : Control
  Dispatcher.UIThread.RunJobs()
  found = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(->(c : Control) { c.Name == name })
  raise "no control #{name}" if found.nil?
  found.not_nil!
end

def click(button : Button)
  button.Command.not_nil!.Execute(nil)
  Dispatcher.UIThread.RunJobs()
end

def open_window(store : SettingsStore) : MainWindow
  window = MainWindow.new
  window.DataContext = MainViewModel.new(store)
  window.Show()
  Dispatcher.UIThread.RunJobs()
  window
end

def headless(dir : String)
  AppBuilder.Configure<App>().UseHeadless(AvaloniaHeadlessPlatformOptions.new).SetupWithoutStarting()
  store = SettingsStore.new(Path.Combine(dir, "settings.json"))
  File.Delete(store.path) if File.Exists(store.path)

  window = open_window(store)
  puts "title: #{window.Title}"
  control(window, "Draft").as(TextBox).Text = "steep the tea"      # typing goes through the two-way binding
  click(control(window, "Add").as(Button))
  control(window, "Draft").as(TextBox).Text = "   "
  click(control(window, "Add").as(Button))                        # blank: ignored
  control(window, "Draft").as(TextBox).Text = "pour"
  click(control(window, "Add").as(Button))
  puts "notes view: #{control(window, "List").as(ListBox).ItemCount} items, \"#{control(window, "Summary").as(TextBlock).Text}\""

  click(control(window, "SettingsTab").as(Button))
  control(window, "UserName").as(TextBox).Text = "Alice"
  control(window, "ShowCount").as(CheckBox).IsChecked = false
  click(control(window, "Save").as(Button))
  puts "settings view: saved=#{File.Exists(store.path)}, title: #{window.Title}"
  click(control(window, "NotesTab").as(Button))
  puts "count visible: #{control(window, "Summary").as(TextBlock).IsVisible}"
  window.Close()

  again = open_window(store)                                    # a new start reads the saved file
  click(control(again, "SettingsTab").as(Button))
  puts "after restart: title: #{again.Title}, name box: #{control(again, "UserName").as(TextBox).Text}"
  again.Close()
end

if ARGV.size > 0 && ARGV[0] == "--headless"
  headless(ARGV.size > 1 ? ARGV[1] : Path.GetTempPath())
else
  AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(ARGV)
end
