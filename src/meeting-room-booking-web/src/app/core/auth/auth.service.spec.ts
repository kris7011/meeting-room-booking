import { TestBed } from '@angular/core/testing';
import type { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import { AuthService, SignInRequiredError } from './auth.service';
import { AUTH_CONFIG_SOURCE, MSAL_CLIENT_FACTORY, MsalClient } from './msal-client';

const CONFIG = {
  clientId: '11111111-2222-3333-4444-555555555555',
  tenant: 'common',
  apiScope: 'api://66666666-7777-8888-9999-000000000000/access_as_user',
};

const ACCOUNT = {
  homeAccountId: 'home',
  name: 'Mia Member',
  username: 'mia@example.com',
} as AccountInfo;

class InteractionRequired extends Error {}

/** A stand-in for MSAL: no network, no Entra. */
function fakeClient(overrides: Partial<MsalClient> = {}): MsalClient {
  let active: AccountInfo | null = null;

  return {
    handleRedirectPromise: vi.fn(async () => null),
    getActiveAccount: () => active,
    getAllAccounts: () => [],
    setActiveAccount: (account) => {
      active = account;
    },
    loginRedirect: vi.fn(async () => undefined),
    logoutRedirect: vi.fn(async () => undefined),
    acquireTokenSilent: vi.fn(
      async () => ({ accessToken: 'access-token' }) as AuthenticationResult,
    ),
    acquireTokenRedirect: vi.fn(async () => undefined),
    isInteractionRequired: (error) => error instanceof InteractionRequired,
    ...overrides,
  };
}

async function start(config: unknown, client: MsalClient): Promise<AuthService> {
  TestBed.configureTestingModule({
    providers: [
      { provide: AUTH_CONFIG_SOURCE, useValue: async () => config },
      { provide: MSAL_CLIENT_FACTORY, useValue: async () => client },
    ],
  });

  const auth = TestBed.inject(AuthService);
  await auth.initialize();

  return auth;
}

describe('AuthService', () => {
  it('is unavailable without a valid configuration, and never contacts MSAL', async () => {
    const client = fakeClient();
    const auth = await start(null, client);

    expect(auth.status()).toBe('unavailable');
    expect(client.handleRedirectPromise).not.toHaveBeenCalled();
    await expect(auth.accessToken()).rejects.toBeInstanceOf(SignInRequiredError);
  });

  it('is unavailable when the configuration cannot be read', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: AUTH_CONFIG_SOURCE, useValue: async () => Promise.reject(new Error('offline')) },
        { provide: MSAL_CLIENT_FACTORY, useValue: async () => fakeClient() },
      ],
    });

    const auth = TestBed.inject(AuthService);
    await auth.initialize();

    expect(auth.status()).toBe('unavailable');
  });

  it('is signed out without an account, and signs in by redirect with the API scope', async () => {
    const client = fakeClient();
    const auth = await start(CONFIG, client);

    expect(auth.status()).toBe('signed-out');

    await auth.signIn();

    expect(client.loginRedirect).toHaveBeenCalledWith({
      scopes: [CONFIG.apiScope],
      prompt: 'select_account',
    });
  });

  it('completes a sign-in that redirected back, and shows the account name', async () => {
    const client = fakeClient({
      handleRedirectPromise: vi.fn(async () => ({ account: ACCOUNT }) as AuthenticationResult),
    });
    const auth = await start(CONFIG, client);

    expect(auth.status()).toBe('signed-in');
    expect(auth.accountName()).toBe('Mia Member');
  });

  it('gets the access token silently for the API scope and the signed-in account', async () => {
    const client = fakeClient({ getActiveAccount: () => ACCOUNT });
    const auth = await start(CONFIG, client);

    expect(await auth.accessToken()).toBe('access-token');
    expect(client.acquireTokenSilent).toHaveBeenCalledWith({
      scopes: [CONFIG.apiScope],
      account: ACCOUNT,
    });
  });

  it('sends the user to Microsoft when the token cannot be renewed silently', async () => {
    const client = fakeClient({
      getActiveAccount: () => ACCOUNT,
      acquireTokenSilent: vi.fn(async () => Promise.reject(new InteractionRequired())),
    });
    const auth = await start(CONFIG, client);

    await expect(auth.accessToken()).rejects.toBeInstanceOf(SignInRequiredError);
    expect(client.acquireTokenRedirect).toHaveBeenCalledWith({
      scopes: [CONFIG.apiScope],
      account: ACCOUNT,
    });
  });

  it('passes other token errors on unchanged', async () => {
    const failure = new Error('network');
    const client = fakeClient({
      getActiveAccount: () => ACCOUNT,
      acquireTokenSilent: vi.fn(async () => Promise.reject(failure)),
    });
    const auth = await start(CONFIG, client);

    await expect(auth.accessToken()).rejects.toBe(failure);
    expect(client.acquireTokenRedirect).not.toHaveBeenCalled();
  });

  it('is signed out at once when signing out, so no further token is handed out', async () => {
    const client = fakeClient({ getActiveAccount: () => ACCOUNT });
    const auth = await start(CONFIG, client);

    const signingOut = auth.signOut();

    expect(auth.status()).toBe('signed-out');
    await expect(auth.accessToken()).rejects.toBeInstanceOf(SignInRequiredError);
    await signingOut;
    expect(client.logoutRedirect).toHaveBeenCalledWith({ account: ACCOUNT });
  });

  it('reports a cancelled sign-in and stays signed out', async () => {
    const client = fakeClient({
      handleRedirectPromise: vi.fn(async () => Promise.reject({ errorCode: 'user_cancelled' })),
    });
    const auth = await start(CONFIG, client);

    expect(auth.status()).toBe('signed-out');
    expect(auth.signInError()).toBe('Sign-in was cancelled.');
  });
});
