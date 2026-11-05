# PROOF.al: written by `alice prove`. Do not edit; run `alice prove` after changing the code or RULES.al.
# Every rule marked proven returned true on every case of its domain. Rules are pure, so this is a proof by
# exhaustion of the program as Alice compiles it. `alice prove --check` fails when the sources change.
#
# alice 1.0.3
# sources sha256 2acf57e997ad3662af70c4ddb9e44581bce9ce75bc3b4afaa92afec04396a8d9
# certificates in PROOF.cert: re-check them with `alice-check PROOF.cert PROOF.al` (no Z3, no compiler)
# rule clamp_is_stable: proven for every input of its domain (smt, cert sha256 9bdd9f6edeede7a5cbd8e52f48ca34be3abf2af3b6e41d1027ac52c4e13b696d)
# rule follow_moves_towards_the_ball: proven for every input of its domain (smt, cert sha256 db5350a56c20f7119f6ad72a3e60bc0782ccabb12bff4a403442a965c0fd5388)
# rule hit_zone_is_bounded_and_symmetric: proven for every input of its domain (smt, cert sha256 ff0ec032f40fda2c99e5a00260cf90d9e311ee3a677d5bdeb6a574af4485de68)
# rule paddle_stays_on_screen: proven for every input of its domain (smt, cert sha256 d6e369ac7f141aee3c0dd6fb298d1472f9f930be3a4139ee4f345f3b03f03a6a)
