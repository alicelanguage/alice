# Runs every API check and exits 1 if any fails: `dotnet apitests/bin/Debug/net10.0/ApiTests.dll` after `alice build`
require "./checks"

checks = ApiChecks.new
failed = checks.run_all
puts "#{checks.passed} passed, #{failed} failed"
exit(failed == 0 ? 0 : 1)
