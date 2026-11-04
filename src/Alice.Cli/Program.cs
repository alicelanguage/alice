// stdout not a terminal (MSBuild Exec, CI, a pipe): diagnostics as plain text on stderr (DiagnosticRenderer.Plain)
Alice.Cli.Ui.DiagnosticRenderer.Plain = Console.IsOutputRedirected;
return Alice.Cli.App.Run(args);
