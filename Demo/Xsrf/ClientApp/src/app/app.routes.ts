import { Routes } from '@angular/router';
import { PAGES_ROUTES } from './pages/pages.routes';

export const routes: Routes = [
  //{ path: '', loadChildren: () => import('./pages/pages.routes').then(m => m.PAGES_ROUTES) },
  { path: '', children: PAGES_ROUTES },
];
