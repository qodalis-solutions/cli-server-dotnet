---
title: Welcome to {{ name }}
order: 1
---
This server shows the public-api, docs and portal plugins of the Qodalis CLI Server.

## Calling the API

Every request carries a host token (`qcli_pat_…`) created by the host application:

```bash
curl -H "Authorization: Bearer qcli_pat_..." {{ baseUrl }}/v1/demo/time
```

Each account may make 1000 requests a day and 60 a minute; the `X-RateLimit-*` headers of every answer show what is
left. Questions: {{ support }}.
