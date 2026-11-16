/** @type {import('jest').Config} */
module.exports = {
  preset: 'jest-preset-angular',
  // Fixes the process time zone to UTC before any worker starts (issue #1292); see
  // jest.global-setup.js for why this, not the invoking shell's TZ, is what every run tests.
  globalSetup: '<rootDir>/jest.global-setup.js',
  setupFilesAfterEnv: ['<rootDir>/setup-jest.ts'],
  transform: {
    '^.+\\.(ts|mjs|js|html)$': [
      'jest-preset-angular',
      {
        tsconfig: '<rootDir>/tsconfig.spec.json',
        stringifyContentPathRegex: '\\.(html|svg)$'
      }
    ]
  },
  // e2e/ is the separate Playwright project (issue #46) with its own runner and its own
  // dependencies; Jest must never try to run or transform it.
  testPathIgnorePatterns: ['<rootDir>/node_modules/', '<rootDir>/dist/', '<rootDir>/e2e/']
};
