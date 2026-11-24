import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test } from '@playwright/test';
import { repositoryRoot } from './foundation-state';

export interface JourneyActor {
  id: string;
  description: string;
}

export interface JourneyStep {
  id: string;
  actor: string;
  intent: string;
  outcome: string;
}

export interface JourneyContract {
  id: string;
  title: string;
  goal: string;
  actors: JourneyActor[];
  preconditions: string[];
  steps: JourneyStep[];
  completion: string[];
}

export function loadJourney(id: string): JourneyContract {
  const path = join(repositoryRoot, 'journeys', `${id}.journey.json`);
  const fail = (problem: string): never => {
    throw new Error(`Journey contract ${path}: ${problem}`);
  };

  let contract: JourneyContract;
  try {
    contract = JSON.parse(readFileSync(path, 'utf-8')) as JourneyContract;
  } catch (error) {
    return fail(`cannot be read (${(error as Error).message}).`);
  }

  if (contract.id !== id) fail(`declares the id "${contract.id}" but was loaded as "${id}".`);
  if (!contract.title || !contract.goal) fail('needs a title and a goal.');
  if (!Array.isArray(contract.actors) || contract.actors.length === 0) fail('has no actors.');
  if (!Array.isArray(contract.steps) || contract.steps.length === 0) fail('has no steps.');
  if (!Array.isArray(contract.preconditions) || !Array.isArray(contract.completion)) fail('needs preconditions and completion lists.');

  const actors = new Set<string>();
  for (const actor of contract.actors) {
    if (!actor.id || !actor.description) fail('has an actor without an id or description.');
    if (actors.has(actor.id)) fail(`declares the actor "${actor.id}" twice.`);
    actors.add(actor.id);
  }

  const steps = new Set<string>();
  for (const step of contract.steps) {
    for (const field of ['id', 'actor', 'intent', 'outcome'] as const) {
      if (!step[field]) fail(`has a step without "${field}".`);
    }
    if (steps.has(step.id)) fail(`declares the step "${step.id}" twice.`);
    if (!actors.has(step.actor)) fail(`step "${step.id}" names the unknown actor "${step.actor}".`);
    steps.add(step.id);
  }

  return contract;
}

export async function journeyStep<T>(journey: JourneyContract, stepId: string, body: () => Promise<T>): Promise<T> {
  const step = journey.steps.find((candidate) => candidate.id === stepId);
  if (!step) {
    throw new Error(`Journey "${journey.id}" has no step "${stepId}"; its steps are ${journey.steps.map((s) => s.id).join(', ')}.`);
  }

  return test.step(`${journey.id} / ${step.id}: ${step.intent}`, async () => {
    try {
      return await body();
    } catch (error) {
      const info = test.info();
      info.annotations.push({ type: 'journey-step-failed', description: `${journey.id} / ${step.id}` });
      const contextPath = info.outputPath('journey-context.json');
      writeFileSync(
        contextPath,
        JSON.stringify({ journey: journey.id, step: step.id, actor: step.actor, intent: step.intent, expectedOutcome: step.outcome }, null, 2),
      );
      await info.attach('journey-context', { path: contextPath, contentType: 'application/json' });
      throw error;
    }
  });
}
