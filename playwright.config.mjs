import projectConfig from './AlmToo/playwright.config.mjs';

export default {
  ...projectConfig,
  testDir: './AlmToo/e2e',
  webServer: {
    ...projectConfig.webServer,
    command: 'dotnet run --project AlmToo/AlmToo.csproj --urls http://127.0.0.1:5208'
  }
};
