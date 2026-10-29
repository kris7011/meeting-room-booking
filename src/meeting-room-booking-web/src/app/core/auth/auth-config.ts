/**
 * Microsoft Entra settings for browser sign-in, read at start-up from
 * `auth-config.json` next to the app (see `auth-config.example.json`).
 * They identify the app registration and the API scope; none of them is a
 * secret. Without a valid file, sign-in is unavailable and the app uses the
 * API's anonymous demo mode as before.
 */
export interface AuthConfig {
  /** Application (client) ID of the public client registration that has this site as an SPA redirect URI. */
  readonly clientId: string;
  /** `common` (work, school and personal accounts), `organizations`, `consumers` or a tenant ID. */
  readonly tenant: string;
  /** The API's delegated scope, `api://<API client ID>/access_as_user`. */
  readonly apiScope: string;
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TENANT_ALIASES = ['common', 'organizations', 'consumers'];
const API_SCOPE = /^api:\/\/[^\s/]+\/access_as_user$/;

/** The settings, or null when the file is missing, incomplete or invalid. */
export function parseAuthConfig(value: unknown): AuthConfig | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const { clientId, tenant, apiScope } = value as Record<string, unknown>;

  if (
    typeof clientId !== 'string' ||
    !GUID.test(clientId) ||
    typeof tenant !== 'string' ||
    !(TENANT_ALIASES.includes(tenant) || GUID.test(tenant)) ||
    typeof apiScope !== 'string' ||
    !API_SCOPE.test(apiScope)
  ) {
    return null;
  }

  return { clientId, tenant, apiScope };
}

/** Reads `auth-config.json`; resolves to null when it is not there. */
export async function fetchAuthConfigFile(): Promise<unknown> {
  const response = await fetch('auth-config.json', { cache: 'no-store' });

  return response.ok ? response.json() : null;
}
