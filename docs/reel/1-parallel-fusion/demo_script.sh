#!/bin/bash
PS1="$ "
cd /work/wt/demo-reel/docs/reel/1-parallel-fusion
sleep 1
echo "$ cat pipeline.al"
sleep 0.5
cat pipeline.al
sleep 2
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll explain pipeline.al"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll explain pipeline.al 2>&1 | head -15
sleep 3
echo "$ # Serial run"
sleep 0.5
echo "$ hyperfine -N -w 1 -r 3 'taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run pipeline.al'"
sleep 0.5
hyperfine -N -w 1 -r 3 "taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run pipeline.al"
sleep 2
echo "$ # Parallel run (all cores)"
sleep 0.5
echo "$ hyperfine -N -w 1 -r 3 'dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run pipeline.al'"
sleep 0.5
hyperfine -N -w 1 -r 3 "dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run pipeline.al"
sleep 2
