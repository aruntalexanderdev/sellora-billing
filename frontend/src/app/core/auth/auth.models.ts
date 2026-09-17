export interface AuthenticatedUser {
  id: string;
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  roleName: string;
  permissions: string[];
}

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  user: AuthenticatedUser;
}

export interface LoginRequest {
  usernameOrEmail: string;
  password: string;
}

export interface ApiProblem {
  title?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
}
