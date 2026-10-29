# Parser regression checks

Run `dotnet run --project tests/ParserRegression` with the .NET 10 SDK.
The harness references the production parser; it does not start Telegram or
make network requests. Synthetic fixtures cover the Astro cards introduced
in October 2026, legacy cards, optional metadata, pagination deduplication,
empty results, and rejecting links to other origins.

For a local diagnostic against a saved public search page, append
`-- /path/to/search.html`. Do not commit downloaded pages or subscriber data.
