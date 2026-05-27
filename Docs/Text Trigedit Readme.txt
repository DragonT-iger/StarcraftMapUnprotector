Text Trigger Plugin by DarkWizzard


FAQ:

Will there be color coding
- yes but not in the near future

Will there be autocomplete
- yes but not in the near future

The tree is buggy
- we will be fixing that soon

Mission briefings dont work
- they aren't meant to work yet

When I clicked on save nothing happened
- click on the checkmark compile triggers button instead...

I found a bug
-thats the point of a beta, report it

It crashed
-send us the save dump and the map and tell us what you were trying to do



*-------------------------------------------------------*
Syntax: all stuff is subject to change ....

Actions:

Victory()

Defeat()

Preserve Trigger()

Wait(duration)

Pause Game()

Unpause Game()

Transmission(display always, string, unit, location A, nummod, duration 2, WAV string, duration)

Play WAV(WAV string, duration)

Display Text Message(string)

Center View(location A)

Create Unit with Properties(player, unit, quantity, location A, properties)

Set Mission Objectives(string)

Set Switch(switch, switchstate)

Set Countdown Timer(nummod, duration)

Run AI Script(aiscript)

Run AI Script At Location(aiscript, location A)

Leader Board Control(string, unit)

Leader Board Control At Location(string, unit, location A)

Leader Board Resources(string, resource type)

Leader Board Kills(string, unit)

Leader Board Points(string, points)

Kill Unit(player, unit)

Kill Unit At Location(player, unit, unitcount, location A)

Remove Unit(player, unit)

Remove Unit At Location(player, unit, unitcount, location A)

Set Resources(player, nummod, quantity 2, resource type)

Set Score(player, nummod, quantity 2, score)

Minimap Ping(location A)

Talking Portrait(unit, duration)

Mute Unit Speech()

Unmute Unit Speech()

Leaderboard Computer Players(actstate)

Leaderboard Goal Control(string, unit, quantity 2)

Leaderboard Goal Control At Location(string, unit, quantity 2, location A)

Leaderboard Goal Resources(string, unit, quantity 2, resource type)

Leaderboard Goal Kills(string, unit, quantity 2)

Leaderboard Goal Points(string, unit, quantity 2)

Move Location(player, unit, location A, location B)

Move Unit(player, unit, unitcount, location A, location B)

Leaderboard Greed(quantity 2)

Set Next Scenario(string)

Set Doodad State(player, unit, location A, actstate)

Set Invincibility(player, unit, location A, actstate)

Create Unit(player, unit, quantity 2, location A)

Set Deaths(player, unit, nummod, quantity 2)

Order(player, unit, location A, location B, order)

Comment(string)

Give Units to Player(player, player 2, unit, unitcount, location A)

Modify Unit Hit Points(player, unit, quantity 2, percent, location A)

Modify Unit Energy(player, unit, quantity 2, percent, location A)

Modify Unit Shield Points(player, unit, quantity 2, percent, location A)

Modify Unit Resource Amount(player, quantity 2, percent, location A)

Modify Unit Hanger Count(player, unit, quantity 2, percent, location A)

Pause Timer()

Unpause Timer()

Draw()

Set Alliance Status(player, ally state)


Conditions:

Countdown Timer(comparison, count)

Command(player, unit, comparison, count)

Bring(player, unit, location, comparison, count)

Accumulate(player, comparison, count, restype)

Kill(player, unit, comparison, count)

Command the Most(unit)

Commands the Most At(unit, location)

Most Kills(unit)

Highest Score(score)

Most Resources(restype)

Switch(switch, switchstate)

Elapsed Time(comparison, count)

Data is a Mission Briefing. Conditions are N/A()

Opponents(player, comparison, count)

Deaths(player, unit, comparison, count)

Command the Least(unit)

Command the Least At(unit, location)

Least Kills(unit)

Lowest Score(score)

Least Resources(restype)

Score(player, score, comparison, count)

Always()

Never()


