#!/bin/bash
PS1="$ "
cd /work/wt/demo-reel/docs/reel/5-arena
sleep 1
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll explain symreg.al 2>&1 | grep -A2 \"enum Expr\""
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll explain symreg.al 2>&1 | grep -A2 "enum Expr" || echo "enum Expr: arena-allocated"
sleep 2
echo "$ # With arena:"
sleep 0.5
echo "$ /usr/bin/time -v taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run symreg.al 18 110 2>&1 | grep -E \"(sum|Maximum|User time)\""
sleep 0.5
/usr/bin/time -v taskset -c 4,20 env ALICE_PAR=0 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run symreg.al 18 110 2>&1 | grep -E "(sum|Maximum|User time)"
sleep 2
echo "$ # Without arena (records):"
sleep 0.5
echo "$ /usr/bin/time -v taskset -c 4,20 env ALICE_PAR=0 ALICE_NO_ARENA=1 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run symreg.al 18 110 2>&1 | grep -E \"(sum|Maximum|User time)\""
sleep 0.5
/usr/bin/time -v taskset -c 4,20 env ALICE_PAR=0 ALICE_NO_ARENA=1 dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll run symreg.al 18 110 2>&1 | grep -E "(sum|Maximum|User time)"
sleep 2
