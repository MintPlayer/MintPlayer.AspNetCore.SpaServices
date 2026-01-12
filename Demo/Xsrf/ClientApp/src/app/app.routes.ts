import { Routes } from '@angular/router';
import { PAGES_ROUTES } from './pages/pages.routes';

export const routes: Routes = [
  { path: '', children: PAGES_ROUTES },
];
