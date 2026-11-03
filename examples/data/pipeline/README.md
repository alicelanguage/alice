# CSV -> aggregate -> JSON

A data pipeline in Alice: read a CSV file (quoted fields, `""` escapes, quoted line breaks), turn rows into typed
records, reject bad rows with their line number, total the sales per region and per month in integer cents, and
write a JSON report with `System.Text.Json`.

| File | Role |
|---|---|
| [data/sales.csv](data/sales.csv) | 12 sales; some product names are quoted (`"Tea pot, glass"`, `"Rooibos ""red"" tea"`) |
| [src/csv.al](src/csv.al) | `Csv.fields` (one record) and `Csv.records` (a document, joining quoted line breaks) |
| [src/report.al](src/report.al) | `Sale`, `Money.parse_cents` / `format`, `Pipeline.parse` / `report` / `json` |
| [src/main.al](src/main.al) | Read a file, print the JSON or write it to a file |
| [test/pipeline_test.al](test/pipeline_test.al) | Parser, money, rejected rows, header check, totals, JSON shape |

Money never touches floating point: `"4.50"` is read as 450 cents, and totals are `Int64`.

## Run

```bash
dotnet run --project src/Alice.Cli -- run examples/data/pipeline                         # bundled data/sales.csv
dotnet run --project src/Alice.Cli -- run examples/data/pipeline -- in.csv report.json   # your file
```

`data/sales.csv` is copied next to the program by `Directory.Build.props`. Output (abridged; the full report also
lists South and the three months):

```json
{
  "source": "sales.csv",
  "orders": 12,
  "revenueCents": 29325,
  "topProduct": "Assam tea",
  "regions": [
    {
      "region": "North",
      "orders": 5,
      "items": 13,
      "revenueCents": 9575,
      "averageOrderCents": 1915
    },
    ...
    {
      "region": "West",
      "orders": 3,
      "items": 15,
      "revenueCents": 13050,
      "averageOrderCents": 4350
    }
  ],
  "months": [
    {
      "month": "2026-01",
      "revenueCents": 10750
    },
    ...
  ],
  "rejected": []
}
```

A row with a missing region, a quantity that is not a positive integer, or a price with more than two decimals
goes to `rejected` as `"record N: ..."`, and the totals skip it.

## Test

```bash
cd examples/data/pipeline && alice test     # 7 examples, 0 failures
```

`test_cents_round_trip_for_every_amount_to_1000` checks `parse_cents(format(c)) == c` for every amount from 0
to 100,000 cents. It is a test loop rather than a `RULES.al` rule because string methods are not accepted as
pure yet (AL0210).

JSON here is `System.Text.Json`; the Alice Core JSON library will replace it when it lands.
