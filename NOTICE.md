# Licensing and provenance

NetworkPlugin is derived from AppShelf, copyright its respective contributors.
Distributed under GNU GPL version 3; see [LICENSE](LICENSE). No warranty.

The standalone network implementation was extracted from AppShelf commit
`76fee373e94679c682a4d440076915338b872055`. Route diagnostics and guarded
recovery derive from AppShelf commit `36dd6e4b1978bac25b918e512092f3436db1dd58`.
Standalone WPF styles derive from AppShelf revision `9daab59a`.

Modified on 2026-11-28 for NetworkPlugin 1.1.0: optional client-independent
VPN diagnostics, explicit local adapter/process selection, consolidated UI
and recovery guards. Only source files were adapted; no additional repository
history was imported. Original GPL terms and license notices are retained.
