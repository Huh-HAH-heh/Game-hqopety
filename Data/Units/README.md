# Unit archetype files

Every custom unit is one editable JSON file: `Data/Units/<id>.unit.json`.

The game loads only `*.unit.json` files directly inside `Data/Units`. Starter files inside `Data/Units/Templates` are examples and are not registered as live unit types.

## Fast workflow

1. Open the body/unit constructor in RimClone.
2. Use **Новый юнит** to get an empty, valid body root, or **Открыть юнита** to continue an existing profile.
3. Build the silhouette in the three projections. Each shape is a region with a stable ID; parents must appear before their children.
4. Edit unit stats, tissues/markers, internal organs and starting equipment in their dedicated tabs.
5. Use **Сохранить файл** to write the complete `.unit.json` into this directory. The file is an ordinary project asset: it can be committed to Git, edited by hand, or filled in by ChatGPT, and opened again in the editor.
6. For iterating with ChatGPT, share the unit's `.unit.json` plus the desired changes. Keep IDs stable where possible; other tissues, organs and markers refer to those IDs.

## File contract

- `schemaVersion`: file-format version. Current version is `1`.
- `id`: stable, lowercase project identifier; 2–64 Latin letters, digits, `.`, `_` and `-`.
- `displayName` / `description`: designer-facing information.
- `runtimeTypeId`: unique numeric ID for the custom archetype. Custom IDs start at `1024`; let the editor allocate one when creating a new profile.
- `stats`: body classification, movement speed, width, length, height, vision range and field of view.
- `regions`: the renderable and damageable skeleton. Each region includes its shape/transform (`part`), hit thickness, damageable tissues and functional markers. Region IDs must be unique and parents must come first.
- `tissues`: independent health pools with weights, HP, optional armor zone and projectile energy/penetration loss. A value of `-1` means inherit the runtime default for projectile losses.
- `markers`: functional points attached to a region. Available roles include `Vision`, `Target`, `Aim`, `Eye`, `Vital`, `Interaction`, `Locomotion`, `WeaponMount` and `Muzzle`. Combine roles as a comma-separated string.
- `organs`: internal hit volumes, each linked to an entry tissue and a target tissue. The entry tissue must be on the organ's parent region.
- `startingEquipment`: initial apparel/armor, durability and a complete armor configuration, including coverage and ballistic/thermal protection.

Coordinates, sizes and follow distances use game-world units. Rotations are radians in the file; the editor shows degrees for convenient editing.

## AI authoring rules

When creating or extending a unit, keep it data-driven: use descriptive stable IDs such as `body.torso`, `body.head`, `limb.front.left` and `organ.heart`; keep every child after its parent; use left/right pairs consistently; align each hit region with the visible shape; add at least one valid tissue per region; and connect organs only to existing tissues. Do not regenerate unrelated IDs when polishing a figure, because stable IDs preserve references and make diffs easy to review.

The blank template is at `Data/Units/Templates/blank.unit.json`. Copy it only as a starting point; assign a new unique `id` and let the editor allocate a fresh `runtimeTypeId` before adding it to `Data/Units`.
