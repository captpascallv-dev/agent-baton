# MAC-PORT-B: independent offline portion

Status: PARTIAL, not full card acceptance. Verified on Intel macOS on 2026-11-12.
Implementation: `66f0a03487c8ec87adc6e986dc52289a975497bf`.
Branch: `mac-port-b`; no main modification, merge or PR.

## Delivered
- Reuse unchanged net8.0 Core parser/mapper/schema serializer in a new CLI. Explicit demo/fixture/live modes, 60-second optional watch, schema-1 single-line stdout and atomic snapshot replacement. Demo/fixture require an explicit output directory. Only primary Codex slot is enabled; five others remain OFF.
- Mac bundle discovery only; Windows discovery and existing application behavior untouched.
- Official SwiftUI/AppKit menu-bar HUD reads only local JSON, never invokes Codex or reads credentials. Null/stale/unavailable values cannot masquerade as current quota; expired reset is stale in the CLI. Countdown does not assume renewal.
- Mac quota_pace default and unknown-value handling; Windows default remains unchanged.
- Official .NET SDK 8.0.428 installed in task-owned user tools, not a system PATH replacement. Existing Swift 6.1.2 used. No third-party UI frameworks or startup items.

## Verification
| Check | Result |
|---|---|
| Core + CLI Release build | PASS, zero warnings/errors; final 8.9s runner, sampled peak 0.29GB |
| CLI portable self-test | PASS arguments, expired reset, parser/mapper, schema1, disabled slots, privacy sentinel, unknown pool exclusion, nulls, invalid JSON/unavailable, atomic replacement/cancellation; 0.5s, 0.04GB |
| quota_pace portable tests | 3/3 PASS, with mocked platform tests |
| Swift Release build | PASS, single job; final 11.9s runner, peak 0.31GB |
| Core demo snapshot consumed by Swift | PASS, 73% primary / 58% weekly, six slots, countdown |
| Actual menu popover light/dark | PASS screenshots below; demo only |
| git diff whitespace check | PASS |

All heavy commands used capped_run, serially; no concurrent compilers. Final CLI and Swift builds used 1.5GB/300s caps, self-test 0.5GB/60s. Earlier full Swift compile peaked 0.73GB and completed in 210.33s. Temporary demo HUD instances were closed after capture.

## Images
Only own app content/status button captured; no desktop/private account content. Both panel images clearly say DEMO. Appearance forced in this app only, not system settings. The menu bar follows system rendering, so own-button crops may look alike despite app-panel appearance.
- [Light real popover](hud/docs/images/mac-hud-demo-light.png)
- [Dark real popover](hud/docs/images/mac-hud-demo-dark.png)
- [Light status button](hud/docs/images/mac-hud-menubar-demo-light.png)
- [Dark status button](hud/docs/images/mac-hud-menubar-demo-dark.png)

## Explicit gaps / approval boundary
- NO actual Codex dispatch, account/rateLimits/read invocation, or comparison with live Codex App quota was performed. Owner's direct approval is still required for these. No model quota was intentionally consumed by these tests.
- Existing Windows/WPF harness was NOT executed. Its project targets net8.0-windows and references the WPF App, with UI/Windows APIs mixed into the executable suite. New portable CLI tests are additional contracts, not a claim that the old suite passed on Mac. Full extraction of those tests is a separate uncompleted task.
- No login/logout/auth-file access, network/VPN/security-setting edits, Windows code changes or other repository edits. No autostart installation or production snapshot was activated.
- The SDK's standard first-run output reported an untrusted ASP.NET development certificate generated automatically. No trust command was run; this was reported to the owner coordinator. Later commands set DOTNET_GENERATE_ASPNET_CERTIFICATE=false. No existing certificates were removed.

## Existing harness cases not run on Mac
All following cases remain unexecuted here because the existing runner is Windows/WPF-bound; these are not marked passing or individually diagnosed as unsupported:
- `token_math_and_overflow`
- `database_saturating_aggregates_before_after_restart`
- `fixture_a_cumulative_equality`
- `fixture_b_exact_duplicate_and_stale_replay`
- `concurrent_interleaving_tuple_oracle`
- `old_frontier_migration_recovers_unique`
- `rotation_partial_truncate_restart`
- `windows_file_id_128_survives_move`
- `same_length_rewrite_is_rescanned`
- `malformed_batch_continues_to_tail`
- `privacy_early_drain_and_all_sinks`
- `session_index_stream_and_malformed_continuation`
- `metadata_unix_time_and_row_continuation`
- `metadata_query_allowlist`
- `session_source_classification_and_persistence`
- `session_hierarchy_rollup_expansion_and_orphans`
- `rollout_path_normalization`
- `implicit_turns_persist_across_restart`
- `implicit_turn_copy_replay_is_idempotent`
- `structural_replay_order_and_generation_reuse`
- `nested_tier_context_and_model_catalog`
- `unchanged_metadata_is_not_rewritten`
- `unchanged_zero_read_write_and_append_only`
- `watcher_prioritizes_dormant_reactivated_log`
- `bounded_scan_resume`
- `quota_durability_stale_reset_and_ambiguity`
- `running_cycle_scope_and_invalid_quota_window`
- `context_continuation_metrics_without_count`
- `continuation_grade_hierarchy`
- `continuation_lifecycle_and_manual_drift`
- `manual_drift_persists_and_clears`
- `context_window_migration_rescan_no_double_count`
- `expired_quota_is_unavailable_and_does_not_define_cycle`
- `refresh_cadence_60_30_8_1`
- `app_server_contract_and_executable_discovery`
- `sqlite_restart_privacy_and_schema`
- `hud_filters_totals_thresholds_and_unknown_cycle`
- `view_model_required_fields_and_privacy`
- `hud_v2_edge_beacon_visual_contract`
- `pin_unpin_persistence`
- `tray_exit_shutdown_coordinator`
- `single_instance_exclusive_lock`
- `s2_1_duplicate_token_replay_state_immutable`
- `s2_1_timestampless_and_order_ties`
- `s2_2_failed_commit_immediate_equals_restart`
- `s2_2_busy_and_revision_conflict_publish_nothing`
- `s2_3_complete_envelope_duplicate_rejection`
- `s2_4_fallback_digest_and_symbolic_settings`
- `s2_4_legacy_absolute_migration_idempotent`
- `s2_5_aggregate_rebuild_resumes`
- `s2_5_incremental_snapshot_cycle_and_frontier`
- `s2_5_frame_built_off_dispatcher`
- `s2_5_performance_100k_250_sessions`
- `s2_6_cross_process_contention_and_kill_recovery`
- `s2_6_gate_error_opens_no_database_or_log`
- `s2_7_recent_degraded_reliable_and_restart`
- `s2_8_huge_unknown_drain_bounded_resume`
- `s2_8_drain_truncate_rewrite_rotation_privacy`
- `correction_03_static_privacy_and_history_scan`
- `r3_01_semantic_alias_duplicates`
- `sr4_01_02_source_repair`
- `sr5_01_context_window_schema_admission`
- `sr5_02_bounded_app_server_streams`
- `r3_02_private_scrub_hard_death_matrix`
- `r3_02_private_scrub_marker_state_matrix`
- `r3_03_duplicate_metadata_enrichment_legacy`
- `r3_03_enrichment_latest_oracle_reverse`
- `r3_04_app_io_fifo_dispatcher_shutdown`
- `r3_04_static_dispatcher_ownership`
- `r3_05_bounded_recurring_turn_queries_20k`
- `r3_06_service_tier_provenance`
- `r3_07_scoped_privacy_evidence`
- `hud_wpf_construction_smoke`
- `hud_wpf_runtime_interactions`
- `hud_v2_1000_row_interaction_stress`
- `hud_v2_left_right_top_dock_and_handle`
- `edge_reveal_z_order`
- `hud_screen_recovery_geometry`
- `package_publication_privacy_contract`
- `lineage_semantic_contract_v9_and_live_replay`
- `lineage_parent_child_grandchild_siblings_unrelated`
- `lineage_pre_cycle_late_parent_missing_cyclic`
- `lineage_incremental_consumers_restart_privacy`
- `lineage_tuple_presence_reelection_and_render`
- `lineage_independent_oracle_defect_injection`
- `provider_quota_cache_isolation`
- `provider_identity_does_not_masquerade`
- `primary_binding_unknown_history`
- `provider_missing_fields_no_allowance_stale_reset`
- `unified_weekly_shared_pool`
- `percentage_presentation`
- `claude_subscription_quota_slot`
- `claude_refresh_stability`
- `failed_provider_does_not_block_ui`
- `codex_home_child_environment_isolation`
- `hud_rail_dock_rapid_click`
- `hud_rail_layout_bounds`
- `hud_topbar_constrained_workarea`
- `primary_verified_attribution_filters_analysis`
- `identity_columns_pragma_only`
- `primary_codex_home_bound_on_child`
- `identity_missing_invalidates_prior_binding`
- `provider_config_switch_drops_cached_quota`
- `provider_disable_drops_live_snapshot`
- `provider_currentboard_ages_live`
- `provider_slow_slot_does_not_block_board`
- `provider_switch_during_refresh_discards_stale`
- `provider_http_body_bounded_and_sanitized`
- `grok_identity_uses_subject_not_token`
- `rail_glance_names_window`
- `provider_live_diagnostic_sanitized`
- `cursor_nested_individual_plan_parser`
- `provider_same_config_refresh_coalesces`
- `engine_late_slot_reaches_snapshot`
- `provider_reset_crossing_ages_before_freshness`
- `quota_identity_change_discards_pair`
- `bound_identity_namespaces_not_mixed`
- `account_identity_nested_chatgpt_envelope`
- `slot_detail_hint_requires_actual_identity`
- `owner_confirmed_primary_restores_analysis`
- `identity_mismatch_does_not_rebind`
- `pi_codex_parser_and_auth_shape`
- `pi_codex_adapter_statuses_and_cache`
- `secondary_cli_home_routes_quota_only`
- `pi_expired_native_renewal_rereads_quota`
- `pi_renewal_single_flight`
- `pi_renewal_coordinator_stamp_tracks_pi_rotation`
- `pi_renewal_timeout_failure_and_login_required`
- `pi_401_retry_once_and_unexpired_unchanged`
- `isolated_preview_requires_data_and_home`
- `grok_auth_shape_issuer_entries`
- `grok_login_unsupported_not_sign_in`
- `grok_expired_renews_then_quota`
- `grok_renewal_rereads_changed_credential`
- `grok_renewal_single_flight`
- `grok_renewal_timeout_and_failure`
- `grok_renewal_revoked_distinct`
- `grok_401_retries_after_refresh`
- `grok_login_stamp_uses_auth_file_not_directory`
- `grok_stable_login_keeps_quota_across_reset`
