import { async, ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonListComponent } from './list.component';

describe('PersonListComponent', () => {
	let component: PersonListComponent;
	let fixture: ComponentFixture<PersonListComponent>;

	beforeEach(async(() => {
		TestBed.configureTestingModule({
			declarations: [PersonListComponent]
		})
			.compileComponents();
	}));

	beforeEach(() => {
		fixture = TestBed.createComponent(PersonListComponent);
		component = fixture.componentInstance;
		fixture.detectChanges();
	});

	it('should create', () => {
		expect(component).toBeTruthy();
	});
});
