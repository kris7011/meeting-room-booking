import { InjectionToken } from '@angular/core';
import type {
  AccountInfo,
  AuthenticationResult,
  EndSessionRequest,
  RedirectRequest,
  SilentRequest,
} from '@azure/msal-browser';
import { AuthConfig, fetchAuthConfigFile } from './auth-config';

/**
 * The part of MSAL's PublicClientApplication that AuthService uses. Tests
 * provide a fake through MSAL_CLIENT_FACTORY, so they never reach Entra.
 */
export interface MsalClient {
  handleRedirectPromise(): Promise<AuthenticationResult | null>;
  getActiveAccount(): AccountInfo | null;
  getAllAccounts(): AccountInfo[];
  setActiveAccount(account: AccountInfo | null): void;
  loginRedirect(request: RedirectRequest): Promise<void>;
  logoutRedirect(request?: EndSessionRequest): Promise<void>;
  acquireTokenSilent(request: SilentRequest): Promise<AuthenticationResult>;
  acquireTokenRedirect(request: RedirectRequest): Promise<void>;
  /** The silent renewal failed because the user must sign in again (MSAL's InteractionRequiredAuthError). */
  isInteractionRequired(error: unknown): boolean;
}

export type MsalClientFactory = (config: AuthConfig) => Promise<MsalClient>;

/**
 * Authorization code flow with PKCE as a public client (no secret). Tokens
 * are cached in sessionStorage, so they end with the browser tab. The
 * redirect URI is this site's own origin, which must be registered as an
 * SPA redirect URI in Entra.
 *
 * MSAL is loaded only here, when sign-in is configured, so the demo mode
 * does not download it.
 */
export const createMsalClient: MsalClientFactory = async (config) => {
  const msal = await import('@azure/msal-browser');

  const application = await msal.createStandardPublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenant}`,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: { cacheLocation: msal.BrowserCacheLocation.SessionStorage },
  });

  return {
    handleRedirectPromise: () => application.handleRedirectPromise(),
    getActiveAccount: () => application.getActiveAccount(),
    getAllAccounts: () => application.getAllAccounts(),
    setActiveAccount: (account) => application.setActiveAccount(account),
    loginRedirect: (request) => application.loginRedirect(request),
    logoutRedirect: (request) => application.logoutRedirect(request),
    acquireTokenSilent: (request) => application.acquireTokenSilent(request),
    acquireTokenRedirect: (request) => application.acquireTokenRedirect(request),
    isInteractionRequired: (error) => error instanceof msal.InteractionRequiredAuthError,
  };
};

export const MSAL_CLIENT_FACTORY = new InjectionToken<MsalClientFactory>('MSAL_CLIENT_FACTORY', {
  providedIn: 'root',
  factory: () => createMsalClient,
});

/** Where the sign-in settings come from; tests replace it. */
export const AUTH_CONFIG_SOURCE = new InjectionToken<() => Promise<unknown>>('AUTH_CONFIG_SOURCE', {
  providedIn: 'root',
  factory: () => fetchAuthConfigFile,
});
