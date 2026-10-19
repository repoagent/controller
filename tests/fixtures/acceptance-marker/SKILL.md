---
name: acceptance-marker
description: Produce the verifiable marker requested by the RepoAgent live acceptance journey when asked to use acceptance-marker.
---

For a requested acceptance marker, run the bundled `marker.py` with the supplied correlation ID. Publish its returned marker through the normal GitHub response skill, in the supplied reply target. Use the script output as the result.
