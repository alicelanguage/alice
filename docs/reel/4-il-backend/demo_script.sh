#!/bin/bash
PS1="$ "
cd /work/wt/demo-reel/docs/reel/4-il-backend
sleep 1
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll emit --il lexer.al 2>&1 | head -20"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll emit --il lexer.al 2>&1 | head -20
sleep 3
echo "$ # IL backend:"
sleep 0.5
echo "$ taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run --backend il lexer.al"
sleep 0.5
taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run --backend il lexer.al 2>&1
sleep 2
echo "$ # C# backend:"
sleep 0.5
echo "$ taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run --backend csharp lexer.al"
sleep 0.5
taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run --backend csharp lexer.al 2>&1
sleep 2
