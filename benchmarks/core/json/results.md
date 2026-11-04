# Alice.Core.Json on the forge (2026-11-03)

## Re-run on 1.0.1 (2026-11-04, commit b2e41c8 (v1.0.1))

`run.sh 3` with `PIN="taskset -c 9,25"`: median of 3 rounds, each the median of 15 timed calls after >= 1 s warm-up,
Alice and System.Text.Json in the same process on the same bytes. Machine: the forge, AWS c7i.8xlarge, Intel Xeon Platinum 8488C (Sapphire Rapids, AVX-512), 32 vCPU, 61 GiB, Arch Linux (kernel 7.2.11), .NET SDK 10.0.114 (runtime 10.0.14); shared box, load average 11.1-13.6.
Raw: `results/raw-20261104T173823Z.txt`.

Changed against the previous run: `json-typed` with the generated mapping (`alice/typed-gen`) is slower on twitter
(1.52 vs 0.64 ms, now behind STJ's 1.22 ms) and citm_catalog (1.30 vs 0.92 ms), and allocates more there; the hand
mapping (`typed-hand`) did not change. Not investigated in this run.

| case | input | engine | ms | GB/s | alloc B/call |
|---|---|---|---:|---:|---:|
| json-dom | canada | alice/dom | 3.3115 | 0.680 | 56 |
| json-dom | canada | stj/JsonDocument | 7.0992 | 0.317 | 72 |
| json-dom | citm_catalog | alice/dom | 0.6670 | 2.590 | 56 |
| json-dom | citm_catalog | stj/JsonDocument | 3.9833 | 0.434 | 72 |
| json-dom | github_events | alice/dom | 0.0174 | 3.743 | 56 |
| json-dom | github_events | stj/JsonDocument | 0.0794 | 0.820 | 72 |
| json-dom | gsoc-2018 | alice/dom | 0.8453 | 3.937 | 56 |
| json-dom | gsoc-2018 | stj/JsonDocument | 3.8630 | 0.862 | 72 |
| json-dom | mesh | alice/dom | 1.1264 | 0.642 | 56 |
| json-dom | mesh | stj/JsonDocument | 2.3882 | 0.303 | 72 |
| json-dom | twitter | alice/dom | 0.2546 | 2.480 | 56 |
| json-dom | twitter | stj/JsonDocument | 0.8473 | 0.745 | 72 |
| json-field-sum | twitter | alice/dom | 0.3064 | 2.061 | 56 |
| json-field-sum | twitter | alice/ondemand | 0.2540 | 2.486 | 0 |
| json-field-sum | twitter | stj/JsonDocument | 0.8579 | 0.736 | 72 |
| json-serialize | canada | alice/writer | 6.6347 | 0.315 | 0 |
| json-serialize | canada | alice/writer-gen | 6.5441 | 0.319 | 0 |
| json-serialize | canada | stj/Utf8JsonWriter | 12.2984 | 0.170 | 0 |
| json-serialize | canada | stj/Utf8JsonWriter-relaxed | 12.2722 | 0.170 | 0 |
| json-serialize | canada | stj/sourcegen | 12.2065 | 0.171 | 0 |
| json-serialize | citm_catalog | alice/writer | 0.4649 | 1.076 | 0 |
| json-serialize | citm_catalog | alice/writer-gen | 0.6895 | 0.726 | 0 |
| json-serialize | citm_catalog | stj/Utf8JsonWriter | 0.6938 | 0.722 | 0 |
| json-serialize | citm_catalog | stj/Utf8JsonWriter-relaxed | 0.6687 | 0.748 | 0 |
| json-serialize | citm_catalog | stj/sourcegen | 0.5007 | 1.001 | 0 |
| json-serialize | twitter | alice/writer | 0.2031 | 1.432 | 0 |
| json-serialize | twitter | alice/writer-gen | 0.3049 | 0.954 | 0 |
| json-serialize | twitter | stj/Utf8JsonWriter | 0.5151 | 0.767 | 0 |
| json-serialize | twitter | stj/Utf8JsonWriter-relaxed | 0.3121 | 0.933 | 0 |
| json-serialize | twitter | stj/sourcegen | 0.4283 | 0.922 | 0 |
| json-typed | canada | alice/typed-gen | 4.7204 | 0.477 | 7143730 |
| json-typed | canada | alice/typed-hand | 5.5567 | 0.405 | 6704594 |
| json-typed | canada | stj/sourcegen | 14.3999 | 0.156 | 8484000 |
| json-typed | citm_catalog | alice/typed-gen | 1.2970 | 1.332 | 1348968 |
| json-typed | citm_catalog | alice/typed-hand | 0.9831 | 1.757 | 1081888 |
| json-typed | citm_catalog | stj/sourcegen | 4.8524 | 0.356 | 1083280 |
| json-typed | twitter | alice/typed-gen | 1.5249 | 0.414 | 365624 |
| json-typed | twitter | alice/typed-hand | 0.6062 | 1.042 | 355000 |
| json-typed | twitter | stj/sourcegen | 1.2160 | 0.519 | 356392 |
| json-validate | canada | alice/stage1 | 0.4597 | 4.897 | 0 |
| json-validate | canada | alice/stage1-avx2 | 0.5049 | 4.458 | 0 |
| json-validate | canada | alice/stage1-avx512 | 0.4495 | 5.008 | 0 |
| json-validate | canada | alice/stage1-scalar | 3.2842 | 0.685 | 0 |
| json-validate | canada | alice/stage1-vector128 | 0.7622 | 2.953 | 0 |
| json-validate | canada | alice/validate | 1.0272 | 2.191 | 0 |
| json-validate | canada | alice/validate-avx2 | 1.0544 | 2.135 | 0 |
| json-validate | canada | alice/validate-avx512 | 1.1109 | 2.026 | 0 |
| json-validate | canada | alice/validate-scalar | 3.8405 | 0.586 | 0 |
| json-validate | canada | alice/validate-vector128 | 1.3696 | 1.644 | 0 |
| json-validate | canada | bcl/Utf8.IsValid | 0.0354 | 63.589 | 0 |
| json-validate | citm_catalog | alice/stage1 | 0.2710 | 6.373 | 0 |
| json-validate | citm_catalog | alice/stage1-avx2 | 0.3264 | 5.292 | 0 |
| json-validate | citm_catalog | alice/stage1-avx512 | 0.2647 | 6.525 | 0 |
| json-validate | citm_catalog | alice/stage1-scalar | 2.7138 | 0.636 | 0 |
| json-validate | citm_catalog | alice/stage1-vector128 | 0.5448 | 3.170 | 0 |
| json-validate | citm_catalog | alice/validate | 0.5157 | 3.349 | 0 |
| json-validate | citm_catalog | alice/validate-avx2 | 0.5818 | 2.969 | 0 |
| json-validate | citm_catalog | alice/validate-avx512 | 0.5222 | 3.308 | 0 |
| json-validate | citm_catalog | alice/validate-scalar | 2.9382 | 0.588 | 0 |
| json-validate | citm_catalog | alice/validate-vector128 | 0.7863 | 2.197 | 0 |
| json-validate | citm_catalog | bcl/Utf8.IsValid | 0.0528 | 32.712 | 0 |
| json-validate | github_events | alice/stage1 | 0.0090 | 7.237 | 0 |
| json-validate | github_events | alice/validate | 0.0163 | 3.996 | 0 |
| json-validate | github_events | bcl/Utf8.IsValid | 0.0013 | 50.102 | 0 |
| json-validate | gsoc-2018 | alice/stage1 | 0.6269 | 5.308 | 0 |
| json-validate | gsoc-2018 | alice/validate | 0.7775 | 4.280 | 0 |
| json-validate | gsoc-2018 | bcl/Utf8.IsValid | 0.0958 | 34.737 | 0 |
| json-validate | mesh | alice/stage1 | 0.1404 | 5.154 | 0 |
| json-validate | mesh | alice/validate | 0.3676 | 1.968 | 0 |
| json-validate | mesh | bcl/Utf8.IsValid | 0.0071 | 101.915 | 0 |
| json-validate | twitter | alice/stage1 | 0.1387 | 4.553 | 0 |
| json-validate | twitter | alice/stage1-avx2 | 0.1423 | 4.438 | 0 |
| json-validate | twitter | alice/stage1-avx512 | 0.1924 | 3.282 | 0 |
| json-validate | twitter | alice/stage1-scalar | 1.4424 | 0.438 | 0 |
| json-validate | twitter | alice/stage1-vector128 | 0.2256 | 2.799 | 0 |
| json-validate | twitter | alice/validate | 0.2264 | 2.789 | 0 |
| json-validate | twitter | alice/validate-avx2 | 0.2232 | 2.829 | 0 |
| json-validate | twitter | alice/validate-avx512 | 0.2157 | 2.928 | 0 |
| json-validate | twitter | alice/validate-scalar | 1.5385 | 0.410 | 0 |
| json-validate | twitter | alice/validate-vector128 | 0.3278 | 1.927 | 0 |
| json-validate | twitter | bcl/Utf8.IsValid | 0.0422 | 14.965 | 0 |
| json-walk | canada | alice/ondemand-walk | 4.6919 | 0.480 | 0 |
| json-walk | canada | stj/Utf8JsonReader | 5.0954 | 0.442 | 0 |
| json-walk | citm_catalog | alice/ondemand-walk | 0.8683 | 1.989 | 0 |
| json-walk | citm_catalog | stj/Utf8JsonReader | 3.5228 | 0.490 | 0 |
| json-walk | github_events | alice/ondemand-walk | 0.0240 | 2.714 | 0 |
| json-walk | github_events | stj/Utf8JsonReader | 0.0649 | 1.004 | 0 |
| json-walk | gsoc-2018 | alice/ondemand-walk | 0.9768 | 3.407 | 0 |
| json-walk | gsoc-2018 | stj/Utf8JsonReader | 3.9245 | 0.848 | 0 |
| json-walk | mesh | alice/ondemand-walk | 1.7122 | 0.423 | 0 |
| json-walk | mesh | stj/Utf8JsonReader | 2.0467 | 0.353 | 0 |
| json-walk | twitter | alice/ondemand-walk | 0.3286 | 1.922 | 0 |
| json-walk | twitter | stj/Utf8JsonReader | 0.6727 | 0.939 | 0 |

## Previous runs

The sections below are the earlier runs, kept as published.

`run.sh 3` (taskset -c 7,23, shared box, load 25-45 during the run): median of 3 rounds, each the median of 15 timed calls after >= 1 s warm-up. GB/s = input bytes (serialize: output bytes) / time. Raw: `results/raw-20261103T022717Z.txt`. Alice and System.Text.Json run in the same process on the same bytes.

| case | input | engine | ms | GB/s | alloc B/call |
|---|---|---|---:|---:|---:|
| json-dom | canada | alice/dom | 3.5040 | 0.642 | 56 |
| json-dom | canada | stj/JsonDocument | 7.0756 | 0.318 | 72 |
| json-dom | citm_catalog | alice/dom | 0.6900 | 2.503 | 56 |
| json-dom | citm_catalog | stj/JsonDocument | 4.2547 | 0.406 | 72 |
| json-dom | github_events | alice/dom | 0.0185 | 3.521 | 56 |
| json-dom | github_events | stj/JsonDocument | 0.0839 | 0.776 | 72 |
| json-dom | gsoc-2018 | alice/dom | 0.8127 | 4.095 | 56 |
| json-dom | gsoc-2018 | stj/JsonDocument | 3.7150 | 0.896 | 72 |
| json-dom | mesh | alice/dom | 1.1298 | 0.640 | 56 |
| json-dom | mesh | stj/JsonDocument | 2.3430 | 0.309 | 72 |
| json-dom | twitter | alice/dom | 0.2431 | 2.598 | 56 |
| json-dom | twitter | stj/JsonDocument | 1.1218 | 0.563 | 72 |
| json-field-sum | twitter | alice/dom | 0.4823 | 1.309 | 56 |
| json-field-sum | twitter | alice/ondemand | 0.4092 | 1.543 | 0 |
| json-field-sum | twitter | stj/JsonDocument | 0.8858 | 0.713 | 72 |
| json-serialize | canada | alice/writer | 7.9933 | 0.262 | 0 |
| json-serialize | canada | alice/writer-gen | 7.2407 | 0.289 | 0 |
| json-serialize | canada | stj/Utf8JsonWriter | 14.6988 | 0.142 | 0 |
| json-serialize | canada | stj/Utf8JsonWriter-relaxed | 13.4076 | 0.156 | 0 |
| json-serialize | canada | stj/sourcegen | 16.1272 | 0.130 | 0 |
| json-serialize | citm_catalog | alice/writer | 0.5091 | 0.983 | 0 |
| json-serialize | citm_catalog | alice/writer-gen | 0.4824 | 1.037 | 0 |
| json-serialize | citm_catalog | stj/Utf8JsonWriter | 0.6747 | 0.743 | 0 |
| json-serialize | citm_catalog | stj/Utf8JsonWriter-relaxed | 0.6444 | 0.776 | 0 |
| json-serialize | citm_catalog | stj/sourcegen | 0.5208 | 0.962 | 0 |
| json-serialize | twitter | alice/writer | 0.1977 | 1.471 | 0 |
| json-serialize | twitter | alice/writer-gen | 0.3026 | 0.961 | 0 |
| json-serialize | twitter | stj/Utf8JsonWriter | 0.5383 | 0.734 | 0 |
| json-serialize | twitter | stj/Utf8JsonWriter-relaxed | 0.3288 | 0.885 | 0 |
| json-serialize | twitter | stj/sourcegen | 0.8960 | 0.441 | 0 |
| json-typed | canada | alice/typed-gen | 5.9661 | 0.377 | 7143728 |
| json-typed | canada | alice/typed-hand | 6.8012 | 0.331 | 6704594 |
| json-typed | canada | stj/sourcegen | 17.2390 | 0.131 | 8484000 |
| json-typed | citm_catalog | alice/typed-gen | 0.9157 | 1.886 | 862048 |
| json-typed | citm_catalog | alice/typed-hand | 0.9820 | 1.759 | 1081888 |
| json-typed | citm_catalog | stj/sourcegen | 7.2044 | 0.240 | 1083280 |
| json-typed | twitter | alice/typed-gen | 0.6446 | 0.980 | 342832 |
| json-typed | twitter | alice/typed-hand | 0.8667 | 0.729 | 355000 |
| json-typed | twitter | stj/sourcegen | 2.1042 | 0.300 | 356392 |
| json-validate | canada | alice/stage1 | 0.4335 | 5.193 | 0 |
| json-validate | canada | alice/stage1-avx2 | 0.5893 | 3.820 | 0 |
| json-validate | canada | alice/stage1-avx512 | 0.4655 | 4.836 | 0 |
| json-validate | canada | alice/stage1-scalar | 3.4321 | 0.656 | 0 |
| json-validate | canada | alice/stage1-vector128 | 0.8909 | 2.527 | 0 |
| json-validate | canada | alice/validate | 1.0600 | 2.124 | 0 |
| json-validate | canada | alice/validate-avx2 | 1.0715 | 2.101 | 0 |
| json-validate | canada | alice/validate-avx512 | 1.2497 | 1.801 | 0 |
| json-validate | canada | alice/validate-scalar | 4.4779 | 0.503 | 0 |
| json-validate | canada | alice/validate-vector128 | 1.7357 | 1.297 | 0 |
| json-validate | canada | bcl/Utf8.IsValid | 0.0422 | 53.342 | 0 |
| json-validate | citm_catalog | alice/stage1 | 0.3219 | 5.366 | 0 |
| json-validate | citm_catalog | alice/stage1-avx2 | 0.3663 | 4.715 | 0 |
| json-validate | citm_catalog | alice/stage1-avx512 | 0.4170 | 4.142 | 0 |
| json-validate | citm_catalog | alice/stage1-scalar | 3.4818 | 0.496 | 0 |
| json-validate | citm_catalog | alice/stage1-vector128 | 0.8762 | 1.971 | 0 |
| json-validate | citm_catalog | alice/validate | 0.4938 | 3.498 | 0 |
| json-validate | citm_catalog | alice/validate-avx2 | 0.5989 | 2.884 | 0 |
| json-validate | citm_catalog | alice/validate-avx512 | 0.7345 | 2.352 | 0 |
| json-validate | citm_catalog | alice/validate-scalar | 3.1098 | 0.555 | 0 |
| json-validate | citm_catalog | alice/validate-vector128 | 0.7981 | 2.164 | 0 |
| json-validate | citm_catalog | bcl/Utf8.IsValid | 0.0584 | 29.575 | 0 |
| json-validate | github_events | alice/stage1 | 0.0092 | 7.080 | 0 |
| json-validate | github_events | alice/validate | 0.0181 | 3.599 | 0 |
| json-validate | github_events | bcl/Utf8.IsValid | 0.0013 | 50.102 | 0 |
| json-validate | gsoc-2018 | alice/stage1 | 0.6361 | 5.232 | 0 |
| json-validate | gsoc-2018 | alice/validate | 1.0293 | 3.233 | 0 |
| json-validate | gsoc-2018 | bcl/Utf8.IsValid | 0.0899 | 37.017 | 0 |
| json-validate | mesh | alice/stage1 | 0.1289 | 5.614 | 0 |
| json-validate | mesh | alice/validate | 0.3380 | 2.141 | 0 |
| json-validate | mesh | bcl/Utf8.IsValid | 0.0071 | 101.915 | 0 |
| json-validate | twitter | alice/stage1 | 0.1350 | 4.678 | 0 |
| json-validate | twitter | alice/stage1-avx2 | 0.1666 | 3.791 | 0 |
| json-validate | twitter | alice/stage1-avx512 | 0.2373 | 2.661 | 0 |
| json-validate | twitter | alice/stage1-scalar | 1.5466 | 0.408 | 0 |
| json-validate | twitter | alice/stage1-vector128 | 0.2538 | 2.488 | 0 |
| json-validate | twitter | alice/validate | 0.2415 | 2.615 | 0 |
| json-validate | twitter | alice/validate-avx2 | 0.2372 | 2.662 | 0 |
| json-validate | twitter | alice/validate-avx512 | 0.4014 | 1.573 | 0 |
| json-validate | twitter | alice/validate-scalar | 1.6094 | 0.392 | 0 |
| json-validate | twitter | alice/validate-vector128 | 0.3683 | 1.715 | 0 |
| json-validate | twitter | bcl/Utf8.IsValid | 0.0561 | 11.257 | 0 |
| json-walk | canada | alice/ondemand-walk | 6.0052 | 0.375 | 0 |
| json-walk | canada | stj/Utf8JsonReader | 5.2263 | 0.431 | 0 |
| json-walk | citm_catalog | alice/ondemand-walk | 0.8555 | 2.019 | 0 |
| json-walk | citm_catalog | stj/Utf8JsonReader | 3.8460 | 0.449 | 0 |
| json-walk | github_events | alice/ondemand-walk | 0.0298 | 2.186 | 0 |
| json-walk | github_events | stj/Utf8JsonReader | 0.0775 | 0.840 | 0 |
| json-walk | gsoc-2018 | alice/ondemand-walk | 0.9358 | 3.556 | 0 |
| json-walk | gsoc-2018 | stj/Utf8JsonReader | 3.7981 | 0.876 | 0 |
| json-walk | mesh | alice/ondemand-walk | 1.6521 | 0.438 | 0 |
| json-walk | mesh | stj/Utf8JsonReader | 2.0385 | 0.355 | 0 |
| json-walk | twitter | alice/ondemand-walk | 0.3758 | 1.681 | 0 |
| json-walk | twitter | stj/Utf8JsonReader | 0.7821 | 0.807 | 0 |

json-typed / json-serialize: `alice/typed-gen` and `alice/writer-gen` are the compiler-generated mapping (C2.5) of the records in `alice/typed.al` (Arrays, Hash), emitted by `alice emit` into `alice/AliceTyped.g.cs`; `alice/typed-hand` and `alice/writer` are the hand-written readers/writers of `Mapping.cs` over the classes of `../baselines/dotnet/Typed.cs` (Lists, Dictionary), the same classes STJ source-gen reads. Checksums are equal across all rows.
