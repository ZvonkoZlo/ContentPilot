import { Routes } from '@angular/router';
import { DashboardComponent } from './dashboard/dashboard.component';
import { CampaignDetailComponent } from './campaign-detail/campaign-detail.component';

export const routes: Routes = [
  { path: '', component: DashboardComponent },
  { path: 'campaign/:id', component: CampaignDetailComponent },
];
