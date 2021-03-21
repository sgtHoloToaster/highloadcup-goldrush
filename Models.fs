module Models

type License = {
    id: int
    digAllowed: int
    digUsed: int
} 

type Area = {
    posX: int
    posY: int
    sizeX: int
    sizeY: int
}

type Dig = {
    licenseID: int
    posX: int
    posY: int
    depth: int
}

let oneBlockArea: Area = { posX = 0; posY = 0; sizeX = 1; sizeY = 1 }

type Explore = {
    area: Area
    amount: int
}

type Wallet = {
    balance: int
    wallet: int seq
}

type Treasure = {
    priority: int
    treasures: string seq
}