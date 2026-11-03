# View models and settings, without a window. The headless UI run is `alice run -- --headless DIR` (see README).
require "../src/settings"
require "../src/viewmodels"
using System.IO
using Notes

def temp_store(name : String) : SettingsStore
  path = Path.Combine(Path.GetTempPath(), "alice-notes-test-#{Environment.ProcessId}", name, "settings.json")
  File.Delete(path) if File.Exists(path)
  SettingsStore.new(path)
end

def test_adding_notes_ignores_blank_drafts_and_clears_the_box
  vm = NotesViewModel.new
  changes = List(String).new
  vm.PropertyChanged += ->(s : Object?, e : System.ComponentModel.PropertyChangedEventArgs) { changes.Add(e.PropertyName.not_nil!) }
  vm.draft = "  green tea "
  vm.add
  vm.draft = "   "
  vm.add
  assert_equal 1, vm.notes.Count
  assert_equal "green tea", vm.notes[0]
  assert_equal "1 note", vm.summary
  assert_equal "   ", vm.draft
  assert changes.includes?("summary")
  vm.clear
  assert_equal "0 notes", vm.summary
end

def test_missing_or_broken_settings_give_defaults
  store = temp_store("broken")
  assert_equal SettingsStore.defaults, store.load
  Directory.CreateDirectory(Path.GetDirectoryName(store.path).not_nil!)
  File.WriteAllText(store.path, "{ not json")
  assert_equal SettingsStore.defaults, store.load
end

def test_settings_round_trip_through_the_file
  store = temp_store("roundtrip")
  store.save(Settings.new("Alice", false))
  assert_equal Settings.new("Alice", false), SettingsStore.new(store.path).load
end

def test_saving_settings_updates_the_title_and_the_notes_page
  store = temp_store("main")
  main = MainViewModel.new(store)
  assert_equal "Notes for friend", main.title
  assert main.current == main.notes
  main.show_settings_command.Execute(nil)
  assert main.current == main.settings
  main.settings.user_name = "  Alice "
  main.settings.show_count = false
  main.settings.save_command.Execute(nil)
  assert_equal "Notes for Alice", main.title
  assert_equal "Alice", main.settings.user_name
  assert !main.notes.show_count
  assert_equal "Notes for Alice", MainViewModel.new(store).title
end

def test_a_blank_name_saves_the_default
  store = temp_store("blank")
  main = MainViewModel.new(store)
  main.settings.user_name = "   "
  main.settings.save_command.Execute(nil)
  assert_equal "friend", store.load.userName
end
