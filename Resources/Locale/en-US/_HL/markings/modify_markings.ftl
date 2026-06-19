verb-categories-undies = Modify Markings

# integrated from floof's modifyundies into HardLight's ModifyMarkings system.
modify-undies-verb-text = {$isVisible ->
*[false] {$putOnVerb}
[true] {$takeOffVerb}
} {$isMine ->
*[false] {$target}'s
[true] your
} {$undies}

marking-toggle-self-start = You start to {$verb} your {$marking-name}.
marking-toggle-self = You {$verb} your {$marking-name}.
marking-toggle-other-start = You start to {$verb} their {$marking-name}.
marking-toggle-other = You {$verb} their {$marking-name}.
marking-toggle-by-other-start = {$other} starts to {$verb} your {$marking-name}.
marking-toggle-by-other = {$other} {$verb} your {$marking-name}.
marking-toggle-self-default-verb-on = put on
marking-toggle-self-default-verb-off = take off
marking-toggle-other-default-verb-on = puts on
marking-toggle-other-default-verb-off = takes off
marking-custom-name = Custom name
visible-at-start = Starts enabled
marking-can-toggle = Can be toggled by you
marking-can-toggle-other = Can be toggled by others
marking-put-on-text-label = Toggle-on verb
marking-take-off-text-label = Toggle-off verb
marking-put-on-text-label-other = Toggle-on verb by others
marking-take-off-text-label-other = Toggle-off verb by others
marking-sample-text = Sample:
marking-show-sample-text = Show sample text
marking-hide-sample-text = Hide sample text

undies-removed-self-start = You start hiding your {$undie}.
undies-equipped-self-start = You start showing your {$undie}.

undies-removed-user-start = You start taking their {$undie} off.
undies-equipped-user-start = You start putting their {$undie} on them.

undies-removed-target-start = {PROPER($user)} starts taking your {$undie} off.
undies-equipped-target-start = {PROPER($user)} starts putting your {$undie} on you.

undies-removed-self = You hide your {$undie}.
undies-equipped-self = You show your {$undie}.

undies-removed-user = You take their {$undie} off.
undies-equipped-user = You put their {$undie} on them.

undies-removed-target = {PROPER($user)} takes your {$undie} off.
undies-equipped-target = {PROPER($user)} puts your {$undie} on you.
