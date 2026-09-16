import { Routes } from '@angular/router';
import { DashboardComponent } from './dashboard/dashboard.component';
import { CampaignDetailComponent } from './campaign-detail/campaign-detail.component';
import { BrandProfileComponent } from './brand-profile/brand-profile.component';

export const routes: Routes = [
  { path: '', component: DashboardComponent },
  { path: 'brand', component: BrandProfileComponent },
  { path: 'campaign/:id', component: CampaignDetailComponent },
];
