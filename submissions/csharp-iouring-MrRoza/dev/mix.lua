-- wrk script: the k6 user model's request mix without think time (closed loop, max throughput).
-- Per k6 iteration: GET /feed, GET /posts/:id (one of the 20 newest), 15% like, 2% create.
--   feed 46.1%, post 46.1%, like 6.9%, create 0.9%
local tokens = {}
for line in io.lines("/w/tokens.txt") do tokens[#tokens + 1] = line end
math.randomseed(os.time())
local n = 0
request = function()
  n = n + 1
  local r = math.random()
  local id = 500001 - math.random(1, 20)
  if r < 0.461 then
    return wrk.format("GET", "/feed")
  elseif r < 0.922 then
    return wrk.format("GET", "/posts/" .. id)
  end
  local tok = tokens[math.random(1, #tokens)]
  local h = { ["Authorization"] = "Bearer " .. tok, ["Content-Type"] = "application/json" }
  if r < 0.991 then
    return wrk.format("POST", "/posts/" .. id .. "/like", h, "")
  end
  return wrk.format("POST", "/posts", h, '{"body":"user says hi at ' .. os.time() .. ' iter ' .. n .. '"}')
end
