//! The slice of `Helpers/Items/ItemHelper.cs` and `Extensions/ItemExtensions.cs` the loot generator
//! leans on: template lookups, base-class tests, item-tree cloning/re-iding, and container sizing.

use std::collections::HashMap;

use super::models::{Item, ItemView, SptLootItem};
use super::mongo_id;

// Base-class tpls, copied verbatim from `Models/Enums/BaseClasses.cs`. They live here rather than in
// their own module because `item_helper` is the only place base classes are ever tested against.

/// `BaseClasses.MONEY`
pub const MONEY: &str = "543be5dd4bdc2deb348b4569";
/// `BaseClasses.AMMO`
pub const AMMO: &str = "5485a8684bdc2da71d8b4567";
/// `BaseClasses.AMMO_BOX`
pub const AMMO_BOX: &str = "543be5cb4bdc2deb348b4568";
/// `BaseClasses.MAGAZINE`
pub const MAGAZINE: &str = "5448bc234bdc2d3c308b4569";
/// `BaseClasses.WEAPON`
pub const WEAPON: &str = "5422acb9af1c889c16000029";
/// `BaseClasses.LAUNCHER`
pub const LAUNCHER: &str = "55818b014bdc2ddc698b456b";
/// `BaseClasses.SPRING_DRIVEN_CYLINDER`
pub const SPRING_DRIVEN_CYLINDER: &str = "627a137bf21bc425b06ab944";
/// `BaseClasses.HEADWEAR`
pub const HEADWEAR: &str = "5a341c4086f77401f2541505";
/// `BaseClasses.VEST`
pub const VEST: &str = "5448e5284bdc2dcb718b4567";
/// `BaseClasses.ARMOR`
pub const ARMOR: &str = "5448e54d4bdc2dcc718b4568";

/// `ItemHelper.GetItem` (`ItemHelper.cs:491-501`) — a plain lookup, absent tpl included.
pub fn get_item<'a>(items_view: &'a HashMap<String, ItemView>, tpl: &str) -> Option<&'a ItemView> {
    items_view.get(tpl)
}

/// `ItemHelper.IsOfBaseclass` (`ItemHelper.cs:296-299`).
pub fn is_of_baseclass(
    items_view: &HashMap<String, ItemView>,
    tpl: &str,
    base_class_tpl: &str,
) -> bool {
    is_of_baseclasses(items_view, tpl, &[base_class_tpl])
}

/// `ItemHelper.IsOfBaseclasses` (`ItemHelper.cs:307-310`).
///
/// C# answers this from `ItemBaseClassService`'s precomputed cache; this walks the parent chain
/// instead, which yields the same answers: `AddBaseItems` (`ItemBaseClassService.cs:71-80`) seeds
/// each item's set from `item.Parent` and climbs until a parent is missing or is itself parentless,
/// so an item is never its own base class, the walk reaches the root node id, and an unknown tpl
/// answers false.
///
/// The one C# behaviour not reproducible here is the `_rootNodeIds` short-circuit — the cache only
/// covers templates with `_type == "Item"`, so C# returns false for a *node* tpl even though its
/// parent chain would match. `ItemView` carries no `_type`, and the loot generator only ever asks
/// about real item tpls, so nothing observable hangs on it.
pub fn is_of_baseclasses(
    items_view: &HashMap<String, ItemView>,
    tpl: &str,
    base_class_tpls: &[&str],
) -> bool {
    let mut current = items_view.get(tpl);

    while let Some(item) = current {
        let parent = match item.parent.as_deref() {
            Some(parent) if !parent.is_empty() => parent,
            // Root node reached, chain exhausted.
            _ => return false,
        };

        if base_class_tpls.contains(&parent) {
            return true;
        }

        // A parent that is not in the view ends the walk, exactly as the C# recursion does.
        current = items_view.get(parent);
    }

    false
}

/// `ItemHelper.ArmorItemCanHoldMods` (`ItemHelper.cs:319-322`) — `_armorSlotsThatCanHoldMods`
/// (`ItemHelper.cs:102`).
pub fn armor_item_can_hold_mods(items_view: &HashMap<String, ItemView>, tpl: &str) -> bool {
    is_of_baseclasses(items_view, tpl, &[HEADWEAR, VEST, ARMOR])
}

/// `ItemExtensions.GetItemWithChildren` (`ItemExtensions.cs:240-278`) — a stack walk that emits the
/// root first and then pops children last-in-first-out, cloning as it goes.
pub fn get_item_with_children(items: &[Item], base_item_id: &str) -> Vec<Item> {
    let mut children_by_parent: HashMap<&str, Vec<&Item>> = HashMap::new();
    let mut root_item = None;

    for item in items {
        if item.id == base_item_id {
            root_item = Some(item);
        }

        if let Some(parent_id) = item.parent_id.as_deref() {
            children_by_parent.entry(parent_id).or_default().push(item);
        }
    }

    // Root not found, nothing to return, exit.
    let Some(root_item) = root_item else {
        return Vec::new();
    };

    let mut result = Vec::new();
    let mut processing_stack = vec![root_item];

    while let Some(current) = processing_stack.pop() {
        result.push(current.clone());

        if let Some(children) = children_by_parent.get(current.id.as_str()) {
            processing_stack.extend(children.iter().copied());
        }
    }

    result
}

/// `ItemExtensions.ReplaceIDs` (`ItemExtensions.cs:394-416`) — sequential in-place walk: each item
/// takes a fresh id and every item still pointing at its old id is reparented, so children listed
/// either side of their parent stay attached.
pub fn replace_ids(items: &mut [Item]) {
    for index in 0..items.len() {
        let new_id = mongo_id::generate();
        let original_id = std::mem::replace(&mut items[index].id, new_id.clone());

        for item in items.iter_mut() {
            if item.parent_id.as_deref() == Some(original_id.as_str()) {
                item.parent_id = Some(new_id.clone());
            }
        }
    }
}

/// `ItemExtensions.RemapRootItemId` (`ItemExtensions.cs:424-448`) — the root is the first element;
/// only its id and its direct children's `parentId` move.
///
/// **Empty input is the C# throw path** — `ItemExtensions.cs:428` dereferences `FirstOrDefault()`
/// unguarded and throws. This returns a fresh id with nothing remapped instead of panicking behind
/// the FFI boundary, so callers must branch to the error/fallback path themselves.
pub fn remap_root_item_id(items: &mut [Item]) -> String {
    let new_id = mongo_id::generate();

    let Some(root_item_existing_id) = items.first().map(|item| item.id.clone()) else {
        return new_id;
    };

    for item in items.iter_mut() {
        if item.id == root_item_existing_id {
            item.id = new_id.clone();

            continue;
        }

        if item.parent_id.as_deref() == Some(root_item_existing_id.as_str()) {
            item.parent_id = Some(new_id.clone());
        }
    }

    new_id
}

/// `ItemHelper.ReparentItemAndChildren` (`ItemHelper.cs:1680-1717`) — re-id the whole tree under
/// `root_item`'s id, then replace element 0 with `root_item` itself. The slice takes a `&mut Vec` at
/// the call site unchanged — clippy rejects the `Vec` in the signature since nothing here resizes.
///
/// **Empty input is the C# throw path** — `ItemHelper.cs:1682` indexes `itemWithChildren[0]`
/// unguarded and throws, which `LocationLootGenerator.cs:1152-1172` catches to log
/// `location-preset_not_found` (naming three production tpls that hit it) before rethrowing. This
/// returns an empty `Vec` rather than panicking behind the FFI boundary, so callers must branch to
/// that error path themselves.
///
/// C# returns the very list it mutated; this returns a **detached deep copy**, so later mutations to
/// the returned `Vec` do not reach the input slice (and vice versa).
pub fn reparent_item_and_children(root_item: &Item, item_with_children: &mut [Item]) -> Vec<Item> {
    let Some(old_root_id) = item_with_children.first().map(|item| item.id.clone()) else {
        return Vec::new();
    };

    let mut id_mappings: HashMap<String, String> = HashMap::new();
    id_mappings.insert(old_root_id, root_item.id.clone());

    for item in item_with_children.iter_mut() {
        let new_id = id_mappings
            .entry(item.id.clone())
            .or_insert_with(mongo_id::generate)
            .clone();

        if let Some(parent_id) = item.parent_id.clone() {
            // A parent outside the list gets a mapping of its own, same as C#.
            item.parent_id = Some(
                id_mappings
                    .entry(parent_id)
                    .or_insert_with(mongo_id::generate)
                    .clone(),
            );
        }

        item.id = new_id;
    }

    // Force the item's details into the first position (C# also logs when the templates differ).
    item_with_children[0] = root_item.clone();

    item_with_children.to_owned()
}

/// `ItemHelper.GetItemSize` (`ItemHelper.cs:1179-1234`) — `(width, height)`. Non-forced child extra
/// size takes the largest per direction, forced extra size sums across every child.
pub fn get_item_size(
    items_view: &HashMap<String, ItemView>,
    items: &[Item],
    root_item_id: &str,
) -> Option<(i32, i32)> {
    let root_item = items.iter().find(|item| item.id == root_item_id)?;
    let root_template = get_item(items_view, &root_item.template)?;

    let width = root_template.width.unwrap_or(0);
    let height = root_template.height.unwrap_or(0);

    let (mut size_up, mut size_down, mut size_left, mut size_right) = (0, 0, 0, 0);
    let (mut forced_up, mut forced_down, mut forced_left, mut forced_right) = (0, 0, 0, 0);

    for item in get_item_with_children(items, root_item_id) {
        // A template missing from the view contributes nothing, matching C#'s null-propagation.
        let Some(item_db_template) = get_item(items_view, &item.template) else {
            continue;
        };

        if item_db_template.extra_size_force_add.unwrap_or(false) {
            // Deviation: C# uses `ExtraSizeUp!.Value` here and throws on a force-add template with a
            // null ExtraSize; unreachable with real data, and a panic behind FFI is worse.
            forced_up += item_db_template.extra_size_up.unwrap_or(0);
            forced_down += item_db_template.extra_size_down.unwrap_or(0);
            forced_left += item_db_template.extra_size_left.unwrap_or(0);
            forced_right += item_db_template.extra_size_right.unwrap_or(0);
        } else {
            size_up = size_up.max(item_db_template.extra_size_up.unwrap_or(0));
            size_down = size_down.max(item_db_template.extra_size_down.unwrap_or(0));
            size_left = size_left.max(item_db_template.extra_size_left.unwrap_or(0));
            size_right = size_right.max(item_db_template.extra_size_right.unwrap_or(0));
        }
    }

    Some((
        width + size_left + size_right + forced_left + forced_right,
        height + size_up + size_down + forced_up + forced_down,
    ))
}

/// `ItemHelper.GetContainerMapping` (`ItemHelper.cs:1771-1786`) plus `GetBlankContainerMap`
/// (`ItemHelper.cs:1794-1798`) — `CellsV` rows of `CellsH` free cells. C# throws
/// `ItemHelperException` when either is missing; this returns the same message as an `Err`.
pub fn get_container_mapping(
    items_view: &HashMap<String, ItemView>,
    container_tpl: &str,
) -> Result<Vec<Vec<u8>>, String> {
    let container_template = get_item(items_view, container_tpl);
    let height = container_template.and_then(|template| template.grid_cells_v);
    let width = container_template.and_then(|template| template.grid_cells_h);

    let (Some(height), Some(width)) = (height, width) else {
        return Err(
            "Height or width is null when trying to calculate container mapping".to_owned(),
        );
    };

    // Rows / columns. Clamped because a negative cell count would abort the process on allocation.
    Ok(vec![
        vec![0u8; width.max(0) as usize];
        height.max(0) as usize
    ])
}

/// `ItemExtensions.ToLootItem` (`ItemExtensions.cs:332-350`) — every field plus the extension data,
/// with a null `composedKey`.
pub fn to_loot_item(item: &Item) -> SptLootItem {
    SptLootItem {
        item: item.clone(),
        composed_key: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    use serde_json::json;

    /// `BaseClasses.ITEM` — the root node every base class below hangs off.
    const ITEM_NODE: &str = "54009119af1c881c07000029";
    const ARMOR_VEST_TPL: &str = "111111111111111111111111";
    const HELMET_TPL: &str = "222222222222222222222222";
    const MOD_PLAIN_A_TPL: &str = "333333333333333333333333";
    const MOD_PLAIN_B_TPL: &str = "444444444444444444444444";
    const MOD_FORCED_A_TPL: &str = "555555555555555555555555";
    const MOD_FORCED_B_TPL: &str = "666666666666666666666666";
    const CONTAINER_TPL: &str = "777777777777777777777777";
    const ORPHAN_TPL: &str = "888888888888888888888888";

    /// Every view is built through serde so the tests exercise the same wire shape the C# caller
    /// sends, rather than a hand-rolled struct literal.
    fn fixture() -> HashMap<String, ItemView> {
        serde_json::from_value(json!({
            // Parent chain: ITEM_NODE <- ARMOR <- ARMOR_VEST_TPL, ITEM_NODE <- HEADWEAR <- HELMET_TPL.
            ITEM_NODE: {},
            ARMOR: { "parent": ITEM_NODE },
            HEADWEAR: { "parent": ITEM_NODE },
            ARMOR_VEST_TPL: { "parent": ARMOR, "width": 3, "height": 4 },
            HELMET_TPL: { "parent": HEADWEAR, "width": 2, "height": 4 },
            // Non-forced mods: the biggest of each direction wins.
            MOD_PLAIN_A_TPL: {
                "parent": HEADWEAR, "extraSizeUp": 1, "extraSizeDown": 0,
                "extraSizeLeft": 1, "extraSizeRight": 0, "extraSizeForceAdd": false
            },
            MOD_PLAIN_B_TPL: {
                "parent": HEADWEAR, "extraSizeUp": 2, "extraSizeDown": 0,
                "extraSizeLeft": 0, "extraSizeRight": 1
            },
            // Forced mods: every direction sums, across items.
            MOD_FORCED_A_TPL: {
                "parent": HEADWEAR, "extraSizeUp": 1, "extraSizeDown": 1,
                "extraSizeLeft": 1, "extraSizeRight": 1, "extraSizeForceAdd": true
            },
            MOD_FORCED_B_TPL: {
                "parent": HEADWEAR, "extraSizeUp": 0, "extraSizeDown": 1,
                "extraSizeLeft": 2, "extraSizeRight": 0, "extraSizeForceAdd": true
            },
            CONTAINER_TPL: { "parent": ITEM_NODE, "gridCellsH": 5, "gridCellsV": 3 },
            // Parent points at a tpl that is not in the view at all.
            ORPHAN_TPL: { "parent": "999999999999999999999999" },
        }))
        .unwrap()
    }

    fn item(id: &str, template: &str, parent_id: Option<&str>) -> Item {
        Item {
            id: id.to_owned(),
            template: template.to_owned(),
            parent_id: parent_id.map(str::to_owned),
            ..Default::default()
        }
    }

    fn ids(items: &[Item]) -> Vec<&str> {
        items.iter().map(|item| item.id.as_str()).collect()
    }

    /// Root `r` with children `a` then `b`; `a` has children `a1` then `a2`.
    fn two_level_tree() -> Vec<Item> {
        vec![
            item("r", HELMET_TPL, None),
            item("a", MOD_PLAIN_A_TPL, Some("r")),
            item("b", MOD_PLAIN_B_TPL, Some("r")),
            item("a1", MOD_FORCED_A_TPL, Some("a")),
            item("a2", MOD_FORCED_B_TPL, Some("a")),
        ]
    }

    #[test]
    fn get_item_returns_the_view_for_known_tpls_only() {
        let view = fixture();

        assert_eq!(get_item(&view, HELMET_TPL).unwrap().width, Some(2));
        assert!(get_item(&view, "999999999999999999999999").is_none());
    }

    #[test]
    fn is_of_baseclass_matches_transitively_through_parents() {
        let view = fixture();

        // Direct parent, then the grandparent two links up.
        assert!(is_of_baseclass(&view, ARMOR_VEST_TPL, ARMOR));
        assert!(is_of_baseclass(&view, ARMOR_VEST_TPL, ITEM_NODE));
        assert!(is_of_baseclass(&view, ARMOR, ITEM_NODE));

        assert!(!is_of_baseclass(&view, ARMOR_VEST_TPL, HEADWEAR));
    }

    /// `ItemBaseClassService.AddBaseItems` (`ItemBaseClassService.cs:71-80`) seeds the cache from
    /// `item.Parent`, never from the item's own id, so an item is never its own base class.
    #[test]
    fn is_of_baseclass_is_not_self_inclusive() {
        let view = fixture();

        assert!(!is_of_baseclass(&view, ARMOR_VEST_TPL, ARMOR_VEST_TPL));
        assert!(!is_of_baseclass(&view, ARMOR, ARMOR));
    }

    #[test]
    fn is_of_baseclass_is_false_for_unknown_tpls_and_unknown_parents() {
        let view = fixture();

        // Tpl missing from the view entirely.
        assert!(!is_of_baseclass(&view, "999999999999999999999999", ARMOR));
        // Tpl present, but its parent chain dead-ends outside the view.
        assert!(!is_of_baseclass(&view, ORPHAN_TPL, ITEM_NODE));
        // A root node has no parent to walk.
        assert!(!is_of_baseclass(&view, ITEM_NODE, ARMOR));
    }

    #[test]
    fn is_of_baseclasses_matches_any_of_the_supplied_classes() {
        let view = fixture();

        assert!(is_of_baseclasses(&view, HELMET_TPL, &[ARMOR, HEADWEAR]));
        assert!(!is_of_baseclasses(&view, HELMET_TPL, &[ARMOR, VEST]));
        assert!(!is_of_baseclasses(&view, HELMET_TPL, &[]));
    }

    #[test]
    fn armor_item_can_hold_mods_covers_headwear_vest_and_armor() {
        let view = fixture();

        assert!(armor_item_can_hold_mods(&view, HELMET_TPL));
        assert!(armor_item_can_hold_mods(&view, ARMOR_VEST_TPL));
        assert!(!armor_item_can_hold_mods(&view, CONTAINER_TPL));
    }

    #[test]
    fn get_item_with_children_returns_the_root_then_lifo_order() {
        let result = get_item_with_children(&two_level_tree(), "r");

        // Children are pushed in input order and popped last-in-first-out.
        assert_eq!(ids(&result), vec!["r", "b", "a", "a2", "a1"]);
    }

    #[test]
    fn get_item_with_children_from_a_mid_tree_node_skips_its_siblings() {
        let result = get_item_with_children(&two_level_tree(), "a");

        assert_eq!(ids(&result), vec!["a", "a2", "a1"]);
    }

    #[test]
    fn get_item_with_children_is_empty_when_the_root_is_missing() {
        assert!(get_item_with_children(&two_level_tree(), "nope").is_empty());
    }

    #[test]
    fn replace_ids_gives_every_item_a_fresh_id_and_keeps_the_hierarchy() {
        let mut items = two_level_tree();
        let original = ids(&items)
            .iter()
            .map(|id| (*id).to_owned())
            .collect::<Vec<_>>();

        replace_ids(&mut items);

        for (index, old_id) in original.iter().enumerate() {
            assert_ne!(&items[index].id, old_id);
            assert_eq!(items[index].id.len(), 24);
        }
        // r <- a, r <- b, a <- a1, a <- a2 all still hold, under the new ids.
        assert_eq!(items[1].parent_id.as_ref(), Some(&items[0].id));
        assert_eq!(items[2].parent_id.as_ref(), Some(&items[0].id));
        assert_eq!(items[3].parent_id.as_ref(), Some(&items[1].id));
        assert_eq!(items[4].parent_id.as_ref(), Some(&items[1].id));
    }

    #[test]
    fn replace_ids_reparents_children_listed_before_their_parent() {
        let mut items = vec![
            item("child", MOD_PLAIN_A_TPL, Some("root")),
            item("root", HELMET_TPL, None),
        ];

        replace_ids(&mut items);

        assert_eq!(items[0].parent_id.as_ref(), Some(&items[1].id));
    }

    #[test]
    fn remap_root_item_id_touches_only_the_root_and_its_direct_children() {
        let mut items = two_level_tree();

        let new_root_id = remap_root_item_id(&mut items);

        assert_eq!(new_root_id.len(), 24);
        assert_eq!(items[0].id, new_root_id);
        assert_eq!(items[1].parent_id.as_deref(), Some(new_root_id.as_str()));
        assert_eq!(items[2].parent_id.as_deref(), Some(new_root_id.as_str()));
        // Everything below the first level is left alone.
        assert_eq!(ids(&items)[1..], ["a", "b", "a1", "a2"]);
        assert_eq!(items[3].parent_id.as_deref(), Some("a"));
        assert_eq!(items[4].parent_id.as_deref(), Some("a"));
    }

    #[test]
    fn reparent_item_and_children_grafts_the_tree_under_a_new_root() {
        let mut items = two_level_tree();
        let root_item = item("new_root", ARMOR_VEST_TPL, None);

        let result = reparent_item_and_children(&root_item, &mut items);

        // Element 0 is replaced wholesale by the supplied root, template and all.
        assert_eq!(result[0].id, "new_root");
        assert_eq!(result[0].template, ARMOR_VEST_TPL);
        // Every child got a fresh id but still points at the new root / its remapped parent.
        for child in &result[1..] {
            assert_eq!(child.id.len(), 24);
        }
        assert_eq!(result[1].parent_id.as_deref(), Some("new_root"));
        assert_eq!(result[2].parent_id.as_deref(), Some("new_root"));
        assert_eq!(result[3].parent_id.as_ref(), Some(&result[1].id));
        assert_eq!(result[4].parent_id.as_ref(), Some(&result[1].id));
        // The in-place list and the returned list agree.
        assert_eq!(ids(&items), ids(&result));
    }

    /// The parent mapping is created on demand (`ItemHelper.cs:1697-1701`), so a child listed before
    /// its parent still lands on the id that parent goes on to take.
    #[test]
    fn reparent_item_and_children_maps_a_parent_listed_after_its_child() {
        let mut items = vec![
            item("r", HELMET_TPL, None),
            item("c", MOD_PLAIN_A_TPL, Some("p")),
            item("p", MOD_PLAIN_B_TPL, Some("r")),
        ];
        let root_item = item("new_root", ARMOR_VEST_TPL, None);

        let result = reparent_item_and_children(&root_item, &mut items);

        // "p" had no mapping yet when "c" was processed, and got that same fresh id when its own
        // turn came.
        assert_eq!(result[2].id.len(), 24);
        assert_ne!(result[2].id, "p");
        assert_eq!(result[1].parent_id.as_ref(), Some(&result[2].id));
        assert_eq!(result[2].parent_id.as_deref(), Some("new_root"));
    }

    #[test]
    fn get_item_size_maxes_non_forced_extra_size_and_sums_forced() {
        let view = fixture();
        let items = two_level_tree();

        // Root 2x4. Non-forced: up max(1,2)=2, down 0, left max(1,0)=1, right max(0,1)=1.
        // Forced: up 1+0=1, down 1+1=2, left 1+2=3, right 1+0=1.
        // Width = 2 + 1 + 1 + 3 + 1, height = 4 + 2 + 0 + 1 + 2.
        let size = get_item_size(&view, &items, "r").unwrap();

        assert_eq!(size, (8, 9));
    }

    #[test]
    fn get_item_size_ignores_items_outside_the_requested_subtree() {
        let view = fixture();
        let items = two_level_tree();

        // "b" has no children, no width/height of its own, and extra size up 2 / right 1 — the
        // sibling "a" branch must not count towards it.
        let size = get_item_size(&view, &items, "b").unwrap();

        assert_eq!(size, (1, 2));
    }

    #[test]
    fn get_item_size_is_none_when_the_root_or_its_template_is_unknown() {
        let view = fixture();
        let items = two_level_tree();

        assert!(get_item_size(&view, &items, "nope").is_none());
        assert!(
            get_item_size(&view, &[item("r", "999999999999999999999999", None)], "r").is_none()
        );
    }

    #[test]
    fn get_container_mapping_builds_blank_rows_of_cells() {
        let map = get_container_mapping(&fixture(), CONTAINER_TPL).unwrap();

        // CellsV rows of CellsH cells, all free.
        assert_eq!(map.len(), 3);
        assert!(map.iter().all(|row| row == &vec![0u8; 5]));
    }

    #[test]
    fn get_container_mapping_errors_without_a_grid() {
        let view = fixture();

        assert!(get_container_mapping(&view, HELMET_TPL).is_err());
        assert!(get_container_mapping(&view, "999999999999999999999999").is_err());
    }

    #[test]
    fn to_loot_item_copies_fields_and_extras_with_no_composed_key() {
        let source: Item = serde_json::from_value(json!({
            "_id": "aaaaaaaaaaaaaaaaaaaaaaaa",
            "_tpl": HELMET_TPL,
            "parentId": "bbbbbbbbbbbbbbbbbbbbbbbb",
            "slotId": "main",
            "location": 3,
            "upd": { "StackObjectsCount": 2 },
            "modAddedField": "kept",
        }))
        .unwrap();

        let loot_item = to_loot_item(&source);

        assert!(loot_item.composed_key.is_none());
        let out = serde_json::to_value(&loot_item).unwrap();
        assert_eq!(out["_id"], "aaaaaaaaaaaaaaaaaaaaaaaa");
        assert_eq!(out["_tpl"], HELMET_TPL);
        assert_eq!(out["parentId"], "bbbbbbbbbbbbbbbbbbbbbbbb");
        assert_eq!(out["slotId"], "main");
        assert_eq!(out["location"], 3);
        assert_eq!(out["upd"]["StackObjectsCount"], 2.0);
        assert_eq!(out["modAddedField"], "kept");
        assert!(out.as_object().unwrap().get("composedKey").is_none());
    }
}
