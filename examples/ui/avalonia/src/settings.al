# Settings persistence: one JSON file. The view models never touch the file system directly.
using System.IO
using System.Text.Json

namespace Notes
  record Settings(userName : String, showCount : Bool)

  class SettingsStore
    getter path : String

    def initialize(@path : String)
    end

    # ~/.config/alice-notes/settings.json on Linux, %APPDATA%\alice-notes\settings.json on Windows.
    def self.default_path : String
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "alice-notes", "settings.json")
    end

    def self.defaults : Settings
      Settings.new("friend", true)
    end

    # A missing or unreadable file gives the defaults: a broken settings file must not stop the app.
    def load : Settings
      return SettingsStore.defaults if !File.Exists(@path)
      begin
        loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(@path))
        loaded.nil? ? SettingsStore.defaults : loaded.not_nil!
      rescue e : JsonException
        SettingsStore.defaults
      end
    end

    def save(settings : Settings)
      dir = Path.GetDirectoryName(@path)
      Directory.CreateDirectory(dir.not_nil!) if !dir.nil? && dir.not_nil! != ""
      File.WriteAllText(@path, JsonSerializer.Serialize(settings))
    end
  end
end
