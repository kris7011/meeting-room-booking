import { parseAuthConfig } from './auth-config';

describe('parseAuthConfig', () => {
  const valid = {
    clientId: '11111111-2222-3333-4444-555555555555',
    tenant: 'common',
    apiScope: 'api://66666666-7777-8888-9999-000000000000/access_as_user',
  };

  it('accepts a complete configuration', () => {
    expect(parseAuthConfig(valid)).toEqual(valid);
  });

  it('accepts the tenant aliases and a tenant ID', () => {
    for (const tenant of ['organizations', 'consumers', '9188040d-6c67-4c5b-b112-36a304b66dad']) {
      expect(parseAuthConfig({ ...valid, tenant })?.tenant).toBe(tenant);
    }
  });

  it('turns a missing or invalid file into "sign-in unavailable"', () => {
    const invalid: unknown[] = [
      null,
      'text',
      {},
      { ...valid, clientId: '<Application (client) ID>' },
      { ...valid, tenant: 'contoso.com' },
      { ...valid, apiScope: 'User.Read' },
      { ...valid, apiScope: 'api://x/other_scope' },
      { ...valid, apiScope: 42 },
    ];

    for (const value of invalid) {
      expect(parseAuthConfig(value)).toBeNull();
    }
  });
});
