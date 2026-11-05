# Qodalis.Cli.Plugin.Logs.Seq

Keeps the server's logs in [Seq](https://datalust.co/seq): every entry is sent there, tagged with the service that
wrote it, and the admin dashboard's **Logs** page reads its history from there. Without this plugin the history is
the admin plugin's in-memory buffer: the latest 1000 entries of one process, gone when it stops. With it the history
survives restarts and outages, and several services (an API and its jobs worker, say) share one log, filtered by
service, level, text and time. The live tail is unchanged.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Logs.Seq
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Logs.Seq.Extensions;

builder.Services
    .AddControllers()
    .AddCli(cli => cli
        .AddAdmin()
        .AddSeqLogs(seq =>
        {
            seq.ServerUrl = "http://seq:5341";
            seq.ApiKey = builder.Configuration["Seq:ApiKey"];
            seq.ServiceName = "ratebell-api";
        }));
```

A service that only writes (a worker without the admin plugin) turns the history off:

```csharp
cli.AddSeqLogs(seq =>
{
    seq.ServerUrl = "http://seq:5341";
    seq.ApiKey = builder.Configuration["Seq:ApiKey"];
    seq.ServiceName = "ratebell-jobs";
    seq.ReadHistory = false;
});
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `ServerUrl` | (required) | The Seq server, an absolute http(s) URL |
| `ApiKey` | none | Sent as `X-Seq-ApiKey`; needs Ingest, and Read when `ReadHistory` is on |
| `ServiceName` | the application name | The `Service` property of every entry, and the dashboard's service filter |
| `MinimumLevel` | `Information` | The lowest level sent; the `Logging` configuration applies too (provider alias `Seq`) |
| `ReadHistory` | `true` | Whether the Logs page reads its history from Seq |
| `QueueLimit` | 10000 | Entries kept while Seq is slow or down; the oldest are dropped beyond it |
| `BatchSize` | 500 | Entries per request |
| `FlushInterval` | 2 s | How long entries wait to go together |
| `RequestTimeout` | 10 s | Timeout of a request to Seq |

## What is sent

Each entry is a CLEF event: `@t`, the rendered message `@m` and its template `@mt`, the level `@l` (`Verbose`,
`Debug`, `Information`, `Warning`, `Error`, `Fatal`), the exception `@x`, `EventId`, the logger category
`SourceContext`, `Service`, and the entry's own values (`{Bank}`, `{Count}`, ...), so Seq can search and chart them.

Logging never waits for Seq and never fails because of it: entries are sent from a background loop, a failed batch is
retried with a growing delay (up to a minute), a batch Seq rejects is dropped, and problems are written to standard
error at most once a minute. On shutdown the queued entries are sent, waiting at most 5 seconds.

## Reading

`GET /api/v1/qcli/logs` (admin plugin, `logs:read`) takes `level` (one or a comma list of `DEBUG`, `INFO`, `WARN`,
`ERROR`), `search` (in the message or the source, ignoring case), `service`, `from` and `to` (ISO 8601), `limit` (at
most 500) and `offset`. With Seq it answers `store: "seq"`, `persistent: true`, the `services` in Seq, and `total: null`
with `hasMore`, because Seq does not count matches; paging reaches 10000 entries back (narrow the filters or the time
range to look further). When Seq cannot be reached the endpoint answers 502 with `{ error }`.

## Running Seq

```yaml
services:
  seq:
    image: datalust/seq:2026.1
    environment:
      ACCEPT_EULA: "Y"
      SEQ_FIRSTRUN_ADMINPASSWORDHASH: ${SEQ_ADMIN_PASSWORD_HASH}
    volumes:
      - seq-data:/data
    mem_limit: 1g
volumes:
  seq-data:
```

Create an API key with Ingest and Read in Seq (Settings > API keys) for the services. Set a retention policy (Settings >
Retention) so the disk does not fill up.
