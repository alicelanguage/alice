namespace Alice.Cli.Commands;

/// <summary>`alice completions bash|zsh|fish|pwsh`: shell tab completion for commands and toolchain subcommands.</summary>
public static class CompletionsCommand
{
    const string Toolchain = "list available install uninstall default pin unpin which show";
    const string Self = "update check uninstall";

    public static int Run(List<string> a)
    {
        string cmds = string.Join(' ', App.CommandNames);
        string script = a.FirstOrDefault() switch
        {
            "bash" => $$"""
                _alice() {
                  local cur="${COMP_WORDS[COMP_CWORD]}" prev="${COMP_WORDS[1]}"
                  if [ "$COMP_CWORD" -eq 1 ]; then COMPREPLY=( $(compgen -W "{{cmds}}" -- "$cur") $(compgen -f -X '!*.al' -- "$cur") ); return; fi
                  case "$prev" in
                    toolchain) [ "$COMP_CWORD" -eq 2 ] && COMPREPLY=( $(compgen -W "{{Toolchain}}" -- "$cur") ) ;;
                    self) [ "$COMP_CWORD" -eq 2 ] && COMPREPLY=( $(compgen -W "{{Self}}" -- "$cur") ) ;;
                    completions) COMPREPLY=( $(compgen -W "bash zsh fish pwsh" -- "$cur") ) ;;
                    *) COMPREPLY=( $(compgen -f -- "$cur") ) ;;
                  esac
                }
                complete -F _alice alice
                """,
            "zsh" => $$"""
                #compdef alice
                _alice() {
                  if (( CURRENT == 2 )); then _alternative 'commands:command:({{cmds}})' 'files:file:_files -g "*.al"'; return; fi
                  case $words[2] in
                    toolchain) (( CURRENT == 3 )) && _values 'subcommand' {{Toolchain}} ;;
                    self) (( CURRENT == 3 )) && _values 'subcommand' {{Self}} ;;
                    completions) _values 'shell' bash zsh fish pwsh ;;
                    *) _files ;;
                  esac
                }
                compdef _alice alice
                """,
            "fish" => $$"""
                complete -c alice -f -n __fish_use_subcommand -a "{{cmds}}"
                complete -c alice -n __fish_use_subcommand -F -a "(__fish_complete_suffix .al)"
                complete -c alice -f -n "__fish_seen_subcommand_from toolchain" -a "{{Toolchain}}"
                complete -c alice -f -n "__fish_seen_subcommand_from self" -a "{{Self}}"
                complete -c alice -f -n "__fish_seen_subcommand_from completions" -a "bash zsh fish pwsh"
                """,
            "pwsh" or "powershell" => $$"""
                Register-ArgumentCompleter -Native -CommandName alice -ScriptBlock {
                  param($word, $ast, $cursor)
                  $parts = $ast.CommandElements | ForEach-Object { $_.ToString() }
                  $list = switch ($parts[1]) {
                    'toolchain' { '{{Toolchain}}'.Split(' ') }
                    'self' { '{{Self}}'.Split(' ') }
                    'completions' { 'bash','zsh','fish','pwsh' }
                    default { '{{cmds}}'.Split(' ') }
                  }
                  if ($parts.Count -gt 2 -or ($parts.Count -eq 2 -and $word -eq '')) { if ($parts[1] -notin 'toolchain','self','completions') { $list = @() } }
                  $list | Where-Object { $_ -like "$word*" } | ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
                }
                """,
            _ => throw new UsageException("usage: alice completions <bash|zsh|fish|pwsh>",
                "bash: eval \"$(alice completions bash)\"  ·  fish: alice completions fish | source"),
        };
        Console.Out.WriteLine(script);
        return 0;
    }
}
