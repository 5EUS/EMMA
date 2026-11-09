# Domain

The Domain layer holds the host's core vocabulary and the rules that need no IO. It has
no dependencies, no clock and no media types: a `Node` is a `Node` whether it is a manga
chapter, an episode or a recipe step.

- **Identity:** `PluginId`, `LocalNodeId`, `NodeRef` (the only node identity the host and
  clients use), `NodeKind`, `AssetRole`, `UserId`.
- **The tree:** `Node`, `NodePage`, `Prop`/`PropValue` (typed, no JSON escape hatch),
  `Query`, filters, `PluginInfo`/`KindInfo`.
- **Assets:** `Asset` with a closed set of deliveries (`InlineDelivery`, `RemoteDelivery`,
  `ProxiedDelivery`), `AssetPayload`, `AssetKey`.
- **User state:** `Progress` with an open-unit `ProgressLocator`, `ProgressGroup` and
  `SeriesProgress` (history by series), `LibraryName`, `LibraryEntry` and `LibrarySummary`
  (named libraries), `AppSettingChange`, `DownloadJob`, `DownloadItem`.
- **Failures:** `Outcome<T>` and `PluginError` for expected failures; exceptions are
  reserved for bugs.
- **Trust:** `CapabilityPolicy` (what one plugin may reach) and `ContractLimits`.

The plugin-facing contract these mirror is `src/EMMA.Contracts/wit` and is explained in
`docs/architecture/contract.md`.
