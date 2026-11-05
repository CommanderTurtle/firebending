# Attribution

firebending-mcp is licensed AGPL-3.0-or-later, the same licence as the firebending repository it
lives in. See `LICENSE`.

`src/store.js` and the file handling in `scripts/configure.mjs` were adapted from
[localflame](https://github.com/can1357/oh-my-pi) (`@commanderturtle/localflame`, AGPL-3.0), the
Firecrawl MCP built for the same harness. What is taken is its verified shape rather than its
subject matter:

- paragraph-aware sectioning that is asserted lossless, so a report can be read by section number
  without ever re-fetching it;
- a compact index (heading, size, referenced files) returned with every result, with the bytes left
  on disk until asked for;
- an indexed search that answers with section numbers and short excerpts;
- a bounded, process-local store with atomic, backed-up writes into a client's own config file.

The web-specific parts were dropped - URL resolution, heading and tag detection, remote image
extraction - and replaced with what a picture report actually contains: the object names the model
was asked about, and the picture files the daemon wrote to disk.

Tool names and replies avoid the vocabulary of the underlying protocol on purpose. The daemon's
routes and JSON fields still carry them (`bboxset`, `bboxask`, `bboxcrop`, `box`), because those are
the wire contract; nothing above the daemon has to know the word.
