import { userAdminMode } from './users.logic';

const has = (...codes: string[]) => (c: string) => codes.includes(c);

describe('userAdminMode', () => {
  it('is company when the company code is held (even with the branch code)', () => {
    expect(userAdminMode(has('Users.Create', 'Branch.Users.Create'), 'Users.Create', 'Branch.Users.Create')).toBe('company');
  });
  it('is branch with only the branch code', () => {
    expect(userAdminMode(has('Branch.Users.Create'), 'Users.Create', 'Branch.Users.Create')).toBe('branch');
  });
  it('is null with neither', () => {
    expect(userAdminMode(has('Users.View'), 'Users.Create', 'Branch.Users.Create')).toBeNull();
  });
});
