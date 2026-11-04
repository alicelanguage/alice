#!/bin/bash
PS1="$ "
cd /work/wt/demo-reel/docs/reel/2-data-race
sleep 1
echo "$ cat race.al"
sleep 0.5
cat race.al
sleep 2
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll compile race.al"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll compile race.al 2>&1
sleep 3
echo "$ # Fixed version:"
sleep 0.5
echo "$ cat race_fixed.al"
sleep 0.5
cat race_fixed.al
sleep 2
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run race_fixed.al"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run race_fixed.al
sleep 2
