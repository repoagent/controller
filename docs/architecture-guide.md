# Interactive architecture guide

Open [the full architecture guide](../.archify/architecture-controller-guide-20261020-120003/index.html) from a local checkout in a browser. It includes seven standalone interactive diagrams covering the system overview, Discussion delivery, auth boundaries, repository-held enrollment approval, token renewal, revoke/uninstall, and deployment.

The implementation evidence is pinned to main commit `d26680680bcbb94c2e3de36317bb61ca6af5b585`. The saved issue snapshot and [approved auth contract](auth-repository-approval.md) describe the subsequent UI-free design. This is a dated architecture snapshot, not a claim that later main changes or planned features are implemented.

The guide folder contains its HTML viewers, diagram candidates, current validation/browser receipts, issue snapshot and Node.js builders. After updating and finalizing diagram candidates through Archify, run `node .archify/architecture-controller-guide-20261020-120003/build-index.mjs` from the repository root to rebuild the guide index. The standalone HTML needs no build step to view.
