 Add a documented AOT validation step or script for milestone 1 (no code artifact currently references the Native AOT check). DONE
 Implement real sandbox enforcement (Milestone 2 deliverable) or document explicit deferral; current managers are placeholders in MacOsPluginSandboxManager.cs:1-13 and peers. DONE
 Add real budget enforcement (Milestone 2 deliverable) beyond logging in PluginBudgetWatcher.cs:1-45. DONE
 Wire the paged pipeline to the plugin host as the default runtime path (Milestone 3 end-to-end requirement); currently only probe endpoints exercise plugin-backed pipeline in ProbeEndpoints.cs:70-190. DONE
 Replace API host in-memory ports with plugin-host-backed ports (Milestone 3 end-to-end requirement); API host still uses in-memory ports in Program.cs:1-120. SOMEWHAT DONE
 Add storage plumbing for page assets and normalized metadata (Milestone 3 item 5); no page-asset storage port or pipeline integration exists yet.
 Define storage keys/paths and retention rules for temp/cached assets (Milestone 3 item 5) and implement cleanup behavior. DONE
 Add end-to-end tests that assert search → chapters → page → cache hit using real plugin host and pipeline (Milestone 3 validation plan); current coverage is probe-level in ProbeEndpointTests.cs:1-197. DONE
 Add a memory budget test or profiling harness for “long chapter read stays within cache budget” (Milestone 3 validation plan); no dedicated test exists yet.