# MVVM: the views bind to these classes, which know nothing about controls. `alice test` checks them headless.
#
# Data binding reads and writes CLR properties, so every bound member is a `getter` or `property`. A view model
# that changes one of its own properties calls `changed("name")` afterwards, and the bound controls re-read it.
# (A `def name=` writer compiles to a method, which bindings do not see.)
using System.Collections.ObjectModel
using System.ComponentModel
using System.Windows.Input

namespace Notes
  class ViewModelBase < INotifyPropertyChanged
    event PropertyChanged : PropertyChangedEventHandler

    def changed(name : String)
      PropertyChanged&.Invoke(self, PropertyChangedEventArgs.new(name))
    end
  end

  # A button command that runs an Alice block.
  class RelayCommand < ICommand
    event CanExecuteChanged : EventHandler

    def initialize(@run : Action)
    end

    def CanExecute(parameter : Object?) : Bool
      true
    end

    def Execute(parameter : Object?)
      @run.Invoke()
    end

    # Tells bound buttons to ask CanExecute again.
    def refresh
      CanExecuteChanged&.Invoke(self, EventArgs.Empty)
    end
  end

  class NotesViewModel < ViewModelBase
    getter notes : ObservableCollection(String)
    getter add_command : RelayCommand
    getter clear_command : RelayCommand
    getter summary : String = "0 notes"
    property draft : String = ""          # two-way: the text box writes it
    property show_count : Bool = true

    def initialize
      @notes = ObservableCollection(String).new
      @add_command = RelayCommand.new(-> { add })
      @clear_command = RelayCommand.new(-> { clear })
    end

    # Blank drafts are ignored; the draft is cleared after adding.
    def add
      text = @draft.strip
      if text.size > 0
        @notes.Add(text)
        @draft = ""
        changed("draft")
        recount
      end
    end

    def clear
      @notes.Clear()
      recount
    end

    def count_visible(value : Bool)
      @show_count = value
      changed("show_count")
    end

    def recount
      @summary = @notes.Count == 1 ? "1 note" : "#{@notes.Count} notes"
      changed("summary")
    end
  end

  class SettingsViewModel < ViewModelBase
    getter save_command : RelayCommand
    getter status : String = ""
    property user_name : String
    property show_count : Bool

    def initialize(@store : SettingsStore, @on_saved : Action(Settings))
      current = @store.load
      @user_name = current.userName
      @show_count = current.showCount
      @save_command = RelayCommand.new(-> { save })
    end

    def save
      name = @user_name.strip.size == 0 ? SettingsStore.defaults.userName : @user_name.strip
      settings = Settings.new(name, @show_count)
      @user_name = name
      changed("user_name")
      @store.save(settings)
      @status = "saved to #{@store.path}"
      changed("status")
      @on_saved.Invoke(settings)
    end
  end

  # The window's view model: which page is shown, and the title built from the settings.
  class MainViewModel < ViewModelBase
    getter notes : NotesViewModel
    getter settings : SettingsViewModel
    getter show_notes_command : RelayCommand
    getter show_settings_command : RelayCommand
    getter current : ViewModelBase
    getter title : String = ""

    def initialize(store : SettingsStore)
      @notes = NotesViewModel.new
      @settings = SettingsViewModel.new(store, ->(s : Settings) { apply(s) })
      @current = @notes
      @show_notes_command = RelayCommand.new(-> { show(@notes) })
      @show_settings_command = RelayCommand.new(-> { show(@settings) })
      apply(store.load)
    end

    def show(page : ViewModelBase)
      @current = page
      changed("current")
    end

    def apply(s : Settings)
      @title = "Notes for #{s.userName}"
      @notes.count_visible(s.showCount)
      changed("title")
    end
  end
end
