export type MembershipRole = 'Owner' | 'Admin' | 'Member';

/** An organisation the signed-in user belongs to (GET /api/organizations). */
export interface Organization {
  readonly publicId: string;
  readonly name: string;
  readonly isLegacy: boolean;
  readonly role: MembershipRole;
}

/** GET /api/me: who the API says the caller is, from the validated access token. */
export interface CurrentUser {
  readonly displayName: string;
  /** The API's own user, or null until this identity has registered. */
  readonly user: { readonly publicId: string; readonly displayName: string } | null;
}

/** POST /api/me/registration. */
export interface RegistrationResponse {
  readonly created: boolean;
  readonly currentUser: CurrentUser;
}
