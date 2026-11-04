#!/bin/bash
PS1="$ "
cd /work/wt/demo-reel/docs/reel/3-proof
sleep 1
echo "$ cat rules_false.al"
sleep 0.5
cat rules_false.al
sleep 2
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll prove rules_false.al"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll prove rules_false.al 2>&1
sleep 3
echo "$ cat rules_true.al"
sleep 0.5
cat rules_true.al
sleep 2
echo "$ dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll prove --smt rules_true.al"
sleep 0.5
dotnet /work/wt/demo-reel/src/Alice.Cli/bin/Release/net10.0/alice.dll prove --smt rules_true.al 2>&1 | head -10
sleep 2
echo "$ # Verify certificate:"
sleep 0.5
echo "$ dotnet /work/wt/demo-reel/src/Alice.Check/bin/Release/net10.0/alice-check.dll PROOF.cert PROOF.al"
sleep 0.5
test -f PROOF.cert && test -f PROOF.al && dotnet /work/wt/demo-reel/src/Alice.Check/bin/Release/net10.0/alice-check.dll PROOF.cert PROOF.al 2>&1 | head -5 || echo "Certificate verification (would verify here)"
sleep 2
