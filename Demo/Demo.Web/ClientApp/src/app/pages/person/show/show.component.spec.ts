import { async, ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonShowComponent } from './show.component';

describe('PersonShowComponent', () => {
	let component: PersonShowComponent;
	let fixture: ComponentFixture<PersonShowComponent>;

	beforeEach(async(() => {
		TestBed.configureTestingModule({
			declarations: [PersonShowComponent]
		})
			.compileComponents();
	}));

	beforeEach(() => {
		fixture = TestBed.createComponent(PersonShowComponent);
		component = fixture.componentInstance;
		fixture.detectChanges();
	});

	it('should create', () => {
		expect(component).toBeTruthy();
	});
});
