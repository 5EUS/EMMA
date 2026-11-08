//! The private, expiring, bounded cache behind `host.cache-get` and `host.cache-put`.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// The most entries one plugin may keep.
const MAX_ENTRIES: usize = 256;
/// The most bytes one plugin may keep.
const MAX_TOTAL_BYTES: usize = 8 * 1024 * 1024;
/// The largest single value.
const MAX_VALUE_BYTES: usize = 1024 * 1024;
/// The longest key, in bytes.
const MAX_KEY_BYTES: usize = 256;

struct Entry {
    value: Vec<u8>,
    expires_at: Instant,
    stored_at: Instant,
}

/// One plugin's cache. Safe to share between calls.
#[derive(Default)]
pub struct PluginCache {
    entries: Mutex<HashMap<String, Entry>>,
}

impl PluginCache {
    /// Reads a value, or `None` when missing or expired.
    ///
    /// # Panics
    /// Only if the internal lock was poisoned by a panic in another thread.
    #[must_use]
    pub fn get(&self, key: &str, now: Instant) -> Option<Vec<u8>> {
        let mut entries = self.entries.lock().expect("cache lock");
        match entries.get(key) {
            Some(entry) if entry.expires_at > now => Some(entry.value.clone()),
            Some(_) => {
                entries.remove(key);
                None
            }
            None => None,
        }
    }

    /// Stores a value. Oversized keys and values are ignored; when full, expired entries go first
    /// and then the oldest.
    ///
    /// # Panics
    /// Only if the internal lock was poisoned by a panic in another thread.
    pub fn put(&self, key: &str, value: Vec<u8>, ttl: Duration, now: Instant) {
        if key.len() > MAX_KEY_BYTES || value.len() > MAX_VALUE_BYTES || ttl.is_zero() {
            return;
        }
        let mut entries = self.entries.lock().expect("cache lock");
        entries.retain(|_, entry| entry.expires_at > now);
        entries.remove(key);
        let mut total: usize = entries.values().map(|entry| entry.value.len()).sum();
        while (entries.len() >= MAX_ENTRIES || total + value.len() > MAX_TOTAL_BYTES)
            && !entries.is_empty()
        {
            let oldest = entries
                .iter()
                .min_by_key(|(_, entry)| entry.stored_at)
                .map(|(name, _)| name.clone());
            if let Some(name) = oldest {
                total -= entries.remove(&name).map_or(0, |entry| entry.value.len());
            }
        }
        entries.insert(
            key.to_owned(),
            Entry {
                value,
                expires_at: now + ttl,
                stored_at: now,
            },
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn values_expire() {
        let cache = PluginCache::default();
        let start = Instant::now();
        cache.put("k", vec![1], Duration::from_secs(10), start);

        assert_eq!(
            cache.get("k", start + Duration::from_secs(9)),
            Some(vec![1])
        );
        assert_eq!(cache.get("k", start + Duration::from_secs(11)), None);
    }

    #[test]
    fn oversized_keys_values_and_zero_ttl_are_ignored() {
        let cache = PluginCache::default();
        let now = Instant::now();
        cache.put(
            &"k".repeat(MAX_KEY_BYTES + 1),
            vec![1],
            Duration::from_secs(1),
            now,
        );
        cache.put(
            "big",
            vec![0; MAX_VALUE_BYTES + 1],
            Duration::from_secs(1),
            now,
        );
        cache.put("zero", vec![1], Duration::ZERO, now);

        assert_eq!(cache.get("big", now), None);
        assert_eq!(cache.get("zero", now), None);
    }

    #[test]
    fn the_oldest_entries_go_first_when_full() {
        let cache = PluginCache::default();
        let start = Instant::now();
        for index in 0..MAX_ENTRIES {
            cache.put(
                &format!("k{index}"),
                vec![0],
                Duration::from_secs(60),
                start + Duration::from_millis(index as u64),
            );
        }

        cache.put(
            "new",
            vec![9],
            Duration::from_secs(60),
            start + Duration::from_secs(1),
        );

        assert_eq!(cache.get("k0", start + Duration::from_secs(2)), None);
        assert_eq!(
            cache.get("new", start + Duration::from_secs(2)),
            Some(vec![9])
        );
        assert!(
            cache
                .get(
                    &format!("k{}", MAX_ENTRIES - 1),
                    start + Duration::from_secs(2)
                )
                .is_some()
        );
    }

    #[test]
    fn total_size_is_bounded() {
        let cache = PluginCache::default();
        let now = Instant::now();
        for index in 0..20 {
            cache.put(
                &format!("k{index}"),
                vec![0; MAX_VALUE_BYTES],
                Duration::from_secs(60),
                now + Duration::from_millis(index),
            );
        }

        let kept = (0..20)
            .filter(|index| {
                cache
                    .get(&format!("k{index}"), now + Duration::from_secs(1))
                    .is_some()
            })
            .count();

        assert!(kept <= MAX_TOTAL_BYTES / MAX_VALUE_BYTES);
        assert!(kept >= 1);
    }
}
