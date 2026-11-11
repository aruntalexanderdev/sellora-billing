import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { ContextService } from './core/context/context.service';
import { contextInterceptor } from './core/http/context.interceptor';
import { correlationInterceptor } from './core/http/correlation.interceptor';
import { errorInterceptor } from './core/http/error.interceptor';
import { platformInterceptor } from './core/platform/platform.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // Order matters: correlation -> platform -> auth -> context -> error (error sees raw responses first, auth retries on top of it).
    // platform only touches /platform/* and /auth/platform/login; auth/context skip those paths (D7).
    provideHttpClient(withInterceptors([correlationInterceptor, platformInterceptor, authInterceptor, contextInterceptor, errorInterceptor])),
    // D1: hydrate the company/branch context (GET /auth/me) before the first navigation; no-op without a session.
    provideAppInitializer(() => inject(ContextService).hydrate()),
  ],
};
