const CAPABILITY_NAME_ENTRY_PATTERN = /\[Capability\.(\w+)\]\s*=\s*"([^"]+)"/g;

const ROLE_CAPABILITY_BLOCK_PATTERN =
  /\[BusinessRole\.(\w+)\]\s*=\s*FrozenSet\.ToFrozenSet\(\s*\[([^\]]*)\]\s*\)/g;

const CAPABILITY_REFERENCE_PATTERN = /Capability\.(\w+)/g;

/**
 * The published `Area.Action` name of every declared `Capability` enum member, read out of
 * `Inventory.Application.Access.Capabilities`'s own source rather than retyped, so a capability
 * renamed on the backend is picked up here too instead of silently falling out of the map.
 */
export function parseCapabilityNamesByEnumMember(capabilitiesSource: string): ReadonlyMap<string, string> {
  const namesByEnumMember = new Map<string, string>();

  for (const match of capabilitiesSource.matchAll(CAPABILITY_NAME_ENTRY_PATTERN)) {
    const [, enumMember, publishedName] = match;
    namesByEnumMember.set(enumMember, publishedName);
  }

  return namesByEnumMember;
}

/**
 * Every `BusinessRole` and the published capability names it grants, read out of
 * `Inventory.Application.Access.RoleCapabilities`'s own `ByRole` table rather than a second,
 * hand-kept copy of it - the actual backend authority a role's frontend description is checked
 * against, not another table somebody has to remember to update alongside it.
 *
 * Throws if no role block is found at all, rather than returning an empty map: a `RoleCapabilities`
 * rewritten in a shape this pattern no longer recognises must fail loudly, not be read as "every
 * role grants nothing" and pass every comparison vacuously.
 */
export function parseBackendRoleCapabilities(
  roleCapabilitiesSource: string,
  capabilityNamesByEnumMember: ReadonlyMap<string, string>
): ReadonlyMap<string, ReadonlySet<string>> {
  const capabilitiesByRole = new Map<string, ReadonlySet<string>>();

  for (const match of roleCapabilitiesSource.matchAll(ROLE_CAPABILITY_BLOCK_PATTERN)) {
    const [, role, capabilityList] = match;
    const capabilityNames = new Set<string>();

    for (const reference of capabilityList.matchAll(CAPABILITY_REFERENCE_PATTERN)) {
      const [, enumMember] = reference;
      const publishedName = capabilityNamesByEnumMember.get(enumMember);
      if (publishedName === undefined) {
        throw new Error(
          `RoleCapabilities grants Capability.${enumMember} to ${role}, which Capabilities has no published name for.`
        );
      }
      capabilityNames.add(publishedName);
    }

    capabilitiesByRole.set(role, capabilityNames);
  }

  if (capabilitiesByRole.size === 0) {
    throw new Error('Found no "[BusinessRole.X] = FrozenSet.ToFrozenSet([...])" entry in RoleCapabilities.');
  }

  return capabilitiesByRole;
}
