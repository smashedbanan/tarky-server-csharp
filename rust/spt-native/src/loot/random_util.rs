//! The draw semantics of `Utils/RandomUtil.cs`, ported bug-for-bug.
//!
//! No RNG-sequence parity with C# is promised — the C# draws come from a CSPRNG, these come from
//! `rand`'s thread-local generator. What is promised is that the *distributions* and the edge-case
//! quirks match, because the loot generator leans on both.

use rand::Rng;

/// A random integer in `min..=max`, inclusive at both ends. `max <= min` yields `min`, matching
/// `RandomUtil.GetInt` (`RandomUtil.cs:35-50`).
pub fn get_int(min: i32, max: i32) -> i32 {
    if max > min {
        rand::rng().random_range(min..=max)
    } else {
        min
    }
}

/// A random float in `[min, max)`, matching `RandomUtil.GetDouble` (`RandomUtil.cs:77-81`).
pub fn get_double(min: f64, max: f64) -> f64 {
    // Same shape as the C#, so an inverted range walks below `min` here too instead of panicking
    // the way `random_range` would.
    min + rand::rng().random::<f64>() * (max - min)
}

/// Whether an event with `chance_percent` (0-100) fires, matching `RandomUtil.GetChance100`
/// (`RandomUtil.cs:145-150`).
///
/// The C# rolls `GetInt(1, 100, exclusive: true)` — an *integer* in 1-99 — so anything under 1%
/// never fires and anything at or above 99% always does. Ported as-is; the loot tables rely on it.
pub fn get_chance_100(chance_percent: f64) -> bool {
    // The C# `Math.Clamp(chance, 0, 100)` is a no-op against a 1-99 roll, so it is not ported.
    f64::from(get_int(1, 99)) <= chance_percent
}

/// A normally distributed draw via the Box-Muller transform, matching
/// `RandomUtil.GetNormallyDistributedRandomNumber` (`RandomUtil.cs:215-246`).
///
/// Negative draws are rerolled; past 100 retries the C# gives up and returns a flat
/// `get_double(0.01, mean * 2)` instead. The C# recurses, this loops — same cap, same fallback.
pub fn get_normally_distributed_random_number(mean: f64, sigma: f64) -> f64 {
    let mut rng = rand::rng();
    let mut attempt = 0;

    loop {
        // The C# CSPRNG helper folds 0 to 1 so it can never hand back 0.0; `rand` can, and
        // `ln(0)` would poison the transform.
        let mut u = 0.0;
        while u == 0.0 {
            u = rng.random::<f64>();
        }

        let mut v = 0.0;
        while v == 0.0 {
            v = rng.random::<f64>();
        }

        let w = (-2.0 * u.ln()).sqrt() * (2.0 * std::f64::consts::PI * v).cos();
        let value_drawn = mean + w * sigma;

        if value_drawn < 0.0 {
            if attempt > 100 {
                return get_double(0.01, mean * 2.0);
            }

            attempt += 1;
            continue;
        }

        return value_drawn;
    }
}

/// A random element of `list`, matching `RandomUtil.GetRandomElement` (`RandomUtil.cs:159-175`).
///
/// # Panics
///
/// If `list` is empty, as the C# throws on an empty collection.
pub fn get_array_value<T>(list: &[T]) -> &T {
    &list[get_int(0, list.len() as i32 - 1) as usize]
}

/// Rounds half to even, matching the default C# `Math.Round(double)`. Ported call sites must use
/// this and never `f64::round`, which rounds halves away from zero.
pub fn round_half_even(value: f64) -> f64 {
    value.round_ties_even()
}

#[cfg(test)]
mod tests {
    use std::collections::HashSet;

    use super::*;

    #[test]
    fn get_int_returns_min_when_max_is_not_above_min() {
        assert_eq!(get_int(5, 5), 5);
        assert_eq!(get_int(5, 3), 5);
        assert_eq!(get_int(-2, -7), -2);
    }

    #[test]
    fn get_int_is_inclusive_at_both_ends() {
        let drawn: HashSet<i32> = (0..1000).map(|_| get_int(1, 3)).collect();
        assert_eq!(drawn, HashSet::from([1, 2, 3]));
    }

    #[test]
    fn get_double_stays_within_the_requested_range() {
        for _ in 0..1000 {
            let value = get_double(2.0, 5.0);
            assert!((2.0..5.0).contains(&value), "{value} is outside [2, 5)");
        }
    }

    #[test]
    fn get_chance_100_never_fires_below_one_percent() {
        // The C# rolls an integer 1-99, so any chance under 1% is unreachable. Bug-compatible.
        for _ in 0..1000 {
            assert!(!get_chance_100(0.0));
            assert!(!get_chance_100(0.5));
        }
    }

    #[test]
    fn get_chance_100_always_fires_at_one_hundred_percent() {
        for _ in 0..1000 {
            assert!(get_chance_100(100.0));
        }
    }

    #[test]
    fn get_normally_distributed_random_number_centres_on_the_mean() {
        let samples: Vec<f64> = (0..10_000)
            .map(|_| get_normally_distributed_random_number(100.0, 10.0))
            .collect();
        let mean = samples.iter().sum::<f64>() / samples.len() as f64;

        assert!((99.0..101.0).contains(&mean), "sample mean {mean} drifted");
        assert!(samples.iter().all(|value| *value >= 0.0), "drew a negative");
    }

    #[test]
    fn get_normally_distributed_random_number_never_returns_negatives() {
        // sigma this far past the mean means nearly every draw is rejected, exercising the reroll
        // loop and its `get_double(0.01, mean * 2)` fallback.
        for _ in 0..100 {
            let value = get_normally_distributed_random_number(1.0, 1000.0);
            assert!(value >= 0.0, "{value} is negative");
        }
    }

    #[test]
    fn get_normally_distributed_random_number_falls_back_once_the_rerolls_run_out() {
        // A mean 1000 sigma below zero can never draw non-negative, so the reroll cap is always
        // reached and the flat `get_double(0.01, mean * 2)` fallback is what comes back. Proves the
        // loop terminates, and pins the C# quirk that the fallback ignores its own sign check.
        for _ in 0..10 {
            let value = get_normally_distributed_random_number(-1000.0, 1.0);
            assert!(
                (-2000.0..=0.01).contains(&value),
                "{value} is not a `get_double(0.01, -2000)` draw"
            );
        }
    }

    #[test]
    fn get_array_value_returns_an_element_of_the_list() {
        let list = ["a", "b", "c"];
        let drawn: HashSet<&str> = (0..1000).map(|_| *get_array_value(&list)).collect();
        assert_eq!(drawn, HashSet::from(["a", "b", "c"]));
    }

    #[test]
    fn round_half_even_rounds_ties_to_even() {
        assert_eq!(round_half_even(0.5), 0.0);
        assert_eq!(round_half_even(1.5), 2.0);
        assert_eq!(round_half_even(2.5), 2.0);
        assert_eq!(round_half_even(-0.5), 0.0);
        assert_eq!(round_half_even(2.4), 2.0);
    }
}
