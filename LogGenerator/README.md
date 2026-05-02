# LogGenerator

`LogGenerator` is an internal developer utility for generating synthetic log files while working on `LogReader`. It is not part of the end-user product or the main product docs split.

Start at the [repo docs hub](../README.md) for the main user and developer guides.

## Build and Run

Run these commands from the `LogGenerator/` folder:

```powershell
dotnet build LogGenerator.sln
dotnet run --project LogGenerator.csproj
```

Use `Dump Lines (%)` to mix long message-dump lines into normal generated logs. `Dump Max Chars` caps each generated dump line at up to 5000 total characters.
