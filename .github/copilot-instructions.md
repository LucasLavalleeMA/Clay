# Copilot instructions for Clay

## Testing
- Write tests with xUnit, use Shouldly for assertions instead of xUnit's `Assert`, and use Moq for mocks (add Moq when a test first needs a mock).
- When a test needs a private member, change it to `internal` and add `InternalsVisibleTo` for the test project; don't use reflection or copy the code into the test.
